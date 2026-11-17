// The VSNeo side of the seeky-engine pipe. The protocol is documented in
// SeekyEngine/EngineServer.cs; this file must agree with it.

namespace VSNeo.Seeky;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

// How a call gets there: starting the engine process, the pipe, the read loop, and the watchdog.
internal sealed partial class SeekyEngineClient
{
    /// <summary>
    /// Sends one request and awaits its reply's <c>result</c>. Cancelling abandons the reply
    /// and tells the engine to stop the work.
    /// </summary>
    private async Task<JsonElement> CallAsync(string op, Action<Utf8JsonWriter>? args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Connection conn = await EnsureConnectedAsync(ct).ConfigureAwait(false);

        long id = Interlocked.Increment(ref nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = new PendingCall(conn, tcs);

        byte[] request = Line(w =>
        {
            w.WriteNumber("id", id);
            w.WriteString("op", op);
            args?.Invoke(w);
        });

        using (ct.Register(() =>
        {
            if (pending.TryRemove(id, out PendingCall? abandoned))
            {
                abandoned.Completion.TrySetCanceled();
                _ = SendAsync(conn, Line(w => { w.WriteString("op", "cancel"); w.WriteNumber("target", id); }));
            }
        }))
        {
            // Cancelled before Register ran: the callback already fired inline and queued a
            // cancel for an id the engine has not seen, which it would ignore and then run
            // the abandoned search in full.
            if (tcs.Task.IsCompleted)
            {
                throw new OperationCanceledException(ct);
            }

            if (!await SendAsync(conn, request).ConfigureAwait(false))
            {
                pending.TryRemove(id, out _);
                throw new SeekyEngineException("the search engine connection was lost");
            }

            return await tcs.Task.ConfigureAwait(false);
        }
    }

    private async Task<Connection> EnsureConnectedAsync(CancellationToken ct)
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(SeekyEngineClient));
        }

        Connection? live = Volatile.Read(ref connection);
        if (live is { IsAlive: true })
        {
            return live;
        }

        await connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(SeekyEngineClient));
            }

            live = connection;
            if (live is { IsAlive: true })
            {
                return live;
            }

            if (IsStopped)
            {
                throw new SeekyEngineException(
                    "the search engine stopped after repeated crashes; restart Visual Studio to retry");
            }

            // Off the caller's thread: a pick tracks from the UI thread, and the file check,
            // process start and job assignment run synchronously up to the first await.
            // The connect itself is not the caller's to cancel - one keystroke's search
            // abandoning it would kill a starting engine that the next search needs.
            Connection started;
            try
            {
                started = await Task.Run(StartEngineAsync).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not SeekyEngineException { Missing: true })
            {
                int count = Interlocked.Increment(ref deaths);
                SeekyLog.Info($"engine: failed to start ({ex.Message}); death {count} of {MaxDeaths}");
                throw;
            }


            bool published;
            lock (connectionSync)
            {
                published = !disposed;
                if (published)
                {
                    Volatile.Write(ref connection, started);
                }
            }

            if (!published)
            {
                started.ClosePipe();
                started.Dispose();
                throw new ObjectDisposedException(nameof(SeekyEngineClient));
            }

            _ = Task.Run(() => ReadLoopAsync(started));
            return started;
        }
        finally
        {
            connectGate.Release();
        }
    }

    private async Task<Connection> StartEngineAsync()
    {
        string exe = enginePath();
        if (!File.Exists(exe))
        {
            throw new SeekyEngineException("the search engine is missing: " + exe) { Missing = true };
        }

        using Process self = Process.GetCurrentProcess();
        string pipeName = $"vsneo-seeky-{self.Id}-{Guid.NewGuid():N}";
        string log = Path.Combine(Path.GetTempPath(), "vsneo-seeky-engine.log");
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = $"--pipe {pipeName} --parent {self.Id} --log \"{log}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? string.Empty,
        };

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, _) => exited.TrySetResult(true);
        process.Start();
        SeekyLog.Info($"engine: started pid {process.Id} on pipe {pipeName}");

        // Before anything can go wrong, as for nvim: the job kills the engine with devenv.
        IDisposable? job = SeekyHost.Current?.AssignToJob(process);
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            // The engine creates the pipe within milliseconds of starting; thirty seconds covers
            // a cold single-file extraction on a slow disk, or Defender scanning the 60 MB exe
            // on first run. A timeout counts as a death, so two of them end the session's engine.
            // An engine that dies before creating its pipe ends the wait at once instead.
            using var stopWaiting = new CancellationTokenSource();
            Task connect = pipe.ConnectAsync(30_000, stopWaiting.Token);
            if (await Task.WhenAny(connect, exited.Task).ConfigureAwait(false) != connect)
            {
                stopWaiting.Cancel();
                throw new SeekyEngineException($"the search engine exited during startup (exit code {SafeExitCode(process)})");
            }

            await connect.ConfigureAwait(false);
        }
        catch
        {
            pipe.Dispose();
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch
            {
                // Already gone.
            }

            process.Dispose();
            job?.Dispose();
            throw;
        }

        return new Connection(process, job, pipe);
    }

    private static string SafeExitCode(Process process)
    {
        try
        {
            return process.WaitForExit(1000) ? process.ExitCode.ToString() : "?";
        }
        catch
        {
            return "?";
        }
    }

    private async Task ReadLoopAsync(Connection conn)
    {
        try
        {
            using var reader = new StreamReader(conn.Pipe, new UTF8Encoding(false), false, 16 * 1024, leaveOpen: true);
            while (true)
            {
                string? line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (line.Length > 0)
                {
                    HandleLine(line);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The engine went away; handled below like a clean end of stream.
        }
        catch (Exception ex)
        {
            SeekyLog.Error("engine: reader failed", ex);
        }

        OnDisconnected(conn);
    }

    private void HandleLine(string line)
    {
        JsonElement root;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            root = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            SeekyLog.Error("engine: unreadable reply", ex);
            return;
        }

        if (root.TryGetProperty("status", out JsonElement note))
        {
            try
            {
                status?.Invoke(note.GetString() ?? string.Empty);
            }
            catch (Exception ex)
            {
                SeekyLog.Error("engine: status handler failed", ex);
            }

            return;
        }

        if (!root.TryGetProperty("id", out JsonElement idElement) || !idElement.TryGetInt64(out long id)
            || !pending.TryRemove(id, out PendingCall? call))
        {
            return; // a reply to a request that was cancelled meanwhile
        }

        if (root.TryGetProperty("error", out JsonElement error))
        {
            call.Completion.TrySetException(new SeekyEngineException(error.GetString() ?? "engine error"));
        }
        else
        {
            call.Completion.TrySetResult(root.TryGetProperty("result", out JsonElement result) ? result : default);
        }
    }

    private void OnDisconnected(Connection conn)
    {
        conn.MarkDead();
        bool expected = disposed;
        int exitCode = conn.TryGetExitCode();
        conn.Dispose();

        foreach (var entry in pending)
        {
            if (!ReferenceEquals(entry.Value.Connection, conn))
            {
                continue;
            }

            if (pending.TryRemove(entry.Key, out PendingCall? call))
            {
                call.Completion.TrySetException(new SeekyEngineException("the search engine exited"));
            }
        }

        if (expected)
        {
            return;
        }

        int count = Interlocked.Increment(ref deaths);
        SeekyLog.Info(
            $"engine: connection lost (exit code {exitCode}); death {count} of {MaxDeaths}"
            + (count >= MaxDeaths ? ", not restarting again this session" : ", the next search restarts it"));
    }

    private async Task<bool> SendAsync(Connection conn, byte[] line)
    {
        await writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!conn.IsAlive)
            {
                return false;
            }

            await conn.Pipe.WriteAsync(line, 0, line.Length).ConfigureAwait(false);
            await conn.Pipe.FlushAsync().ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            return false;
        }
        finally
        {
            writeGate.Release();
        }
    }

    /// <summary>One request line: an object whose members <paramref name="members"/> writes, then '\n'.</summary>
    private static byte[] Line(Action<Utf8JsonWriter> members)
    {
        using var buffer = new MemoryStream(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            members(w);
            w.WriteEndObject();
        }

        buffer.WriteByte((byte)'\n');
        return buffer.ToArray();
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.TryGetInt32(out int n) ? n : 0;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.True;

    private static SeekyRange[] Ranges(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out JsonElement list) || list.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<SeekyRange>();
        }

        var ranges = new List<SeekyRange>();
        foreach (JsonElement pair in list.EnumerateArray())
        {
            if (pair.ValueKind == JsonValueKind.Array && pair.GetArrayLength() == 2)
            {
                ranges.Add(new SeekyRange(pair[0].GetInt32(), pair[1].GetInt32()));
            }
        }

        return ranges.ToArray();
    }

    /// <summary>One engine process and the pipe to it.</summary>
    private sealed class Connection : IDisposable
    {
        private readonly Process process;
        private readonly IDisposable? job;
        private int dead;
        private int disposedFlag;

        internal Connection(Process process, IDisposable? job, NamedPipeClientStream pipe)
        {
            this.process = process;
            this.job = job;
            Pipe = pipe;
        }

        internal NamedPipeClientStream Pipe { get; }

        internal int ProcessId => process.Id;

        internal bool IsAlive => Volatile.Read(ref dead) == 0;

        internal void MarkDead() => Volatile.Write(ref dead, 1);

        internal int TryGetExitCode()
        {
            try
            {
                return process.WaitForExit(2000) ? process.ExitCode : -1;
            }
            catch
            {
                return -1;
            }
        }

        internal void ClosePipe()
        {
            MarkDead();
            try
            {
                Pipe.Dispose();
            }
            catch
            {
                // Best-effort teardown.
            }
        }

        /// <summary>Blocks up to two seconds for the exit; call off the UI thread.</summary>
        public void Dispose()
        {
            // Twice by design on teardown: the client's Dispose and the reader's disconnect.
            if (Interlocked.Exchange(ref disposedFlag, 1) != 0)
            {
                return;
            }

            ClosePipe();

            // A clean exit follows the pipe closing; the job is the backstop for one that
            // does not (and disposing it kills whatever is still running).
            if (!process.WaitForExit(2000))
            {
                SeekyLog.Info("engine: did not exit after its pipe closed; the job ends it");
            }

            job?.Dispose();
            process.Dispose();
        }
    }
}
