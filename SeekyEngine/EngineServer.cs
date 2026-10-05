// The engine side of the VSNeo <-> seeky-engine pipe.
//
// Protocol: UTF-8 JSON, one object per line, both directions.
//
//   client -> engine   {"id":7,"op":"grep","query":"foo","mode":"fuzzy","max":100}
//                      {"op":"cancel","target":7}            (no id, no reply)
//   engine -> client   {"id":7,"result":{...}}
//                      {"id":7,"error":"message"}            (a cancelled request too)
//                      {"status":"indexing… 1200 files"}     (unsolicited, for the status line)
//
// Ops and their results (paths are workspace-relative, '/' separators, as fff
// reports them; ranges are [start, end) UTF-16 char indices):
//
//   start     dir                     -> null       ensures an index for dir, waits for the scan
//   refreshGit                        -> null
//   history   max                     -> ["query", ...]
//   files     query currentFile? max match?
//                                     -> {items:[File], location:{line,col}|null}
//                                        match: fuzzy (default) | plain | glob
//   mixed     query currentFile? max  -> {items:[File + isDirectory], location}
//   git       query max               -> [File]
//   grep      query mode max          -> {matches:[Match], regexError}
//                                        mode: plain | regex | fuzzy | any
//   symbols   dir query max           -> [Symbol]
//   track     query path              -> null       frecency + history learning
//
//   File   {path, frecency, gitStatus, isBinary}
//   Match  {path, line, col, text, ranges, gitStatus, isBinary, isDefinition}
//   Symbol {path, line, col, text, kind, name, nameRanges, gitStatus, isBinary}
//
// Requests run concurrently on the thread pool; FffNativeClient serializes the
// native calls itself. Replies go out in completion order, matched by id.

namespace SeekyVS;

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

internal sealed class EngineServer : IDisposable
{
    private readonly FffNativeClient client = new();
    private readonly ConcurrentDictionary<long, CancellationTokenSource> inFlight = new();
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private Stream? stream;

    /// <summary>Serves one client on <paramref name="pipeName"/> until it disconnects.</summary>
    public async Task RunAsync(string pipeName)
    {
        using var pipe = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await pipe.WaitForConnectionAsync();
        SeekyLog.Info("engine: client connected");
        await ServeAsync(pipe);
    }

    /// <summary>Reads requests off <paramref name="duplex"/> until end of stream.</summary>
    internal async Task ServeAsync(Stream duplex)
    {
        stream = duplex;
        using var reader = new StreamReader(duplex, new UTF8Encoding(false), false, 16 * 1024, leaveOpen: true);
        while (true)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync();
            }
            catch (IOException)
            {
                break; // the client went away mid-line
            }

            if (line is null)
            {
                break;
            }

            if (line.Length == 0)
            {
                continue;
            }

            Dispatch(line);
        }

        foreach (CancellationTokenSource cts in inFlight.Values)
        {
            cts.Cancel();
        }
    }

    private void Dispatch(string line)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch (JsonException ex)
        {
            SeekyLog.Error("engine: unreadable request", ex);
            return;
        }

        JsonElement root = doc.RootElement;
        string op = Str(root, "op") ?? string.Empty;
        if (op == "cancel")
        {
            if (root.TryGetProperty("target", out JsonElement t) && t.TryGetInt64(out long target)
                && inFlight.TryGetValue(target, out CancellationTokenSource? victim))
            {
                victim.Cancel();
            }

            doc.Dispose();
            return;
        }

        if (!root.TryGetProperty("id", out JsonElement idElement) || !idElement.TryGetInt64(out long id))
        {
            SeekyLog.Info($"engine: request without id ignored (op '{op}')");
            doc.Dispose();
            return;
        }

        var cts = new CancellationTokenSource();
        inFlight[id] = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                byte[] reply = await HandleAsync(id, op, root, cts.Token);
                await SendAsync(reply);
            }
            catch (Exception ex)
            {
                if (ex is not OperationCanceledException)
                {
                    SeekyLog.Error($"engine: {op} failed", ex);
                }

                await SendAsync(Write(w =>
                {
                    w.WriteNumber("id", id);
                    w.WriteString("error", ex is OperationCanceledException ? "cancelled" : ex.Message);
                }));
            }
            finally
            {
                inFlight.TryRemove(id, out _);
                cts.Dispose();
                doc.Dispose();
            }
        });
    }

    private async Task<byte[]> HandleAsync(long id, string op, JsonElement req, CancellationToken ct)
    {
        string query = Str(req, "query") ?? string.Empty;
        int max = Int(req, "max", 100);
        switch (op)
        {
            case "start":
                await client.StartAsync(Str(req, "dir") ?? throw new ArgumentException("start needs dir"), PostStatus, ct);
                return Result(id, w => w.WriteNullValue());

            case "refreshGit":
                await client.RefreshGitStatusAsync(ct);
                return Result(id, w => w.WriteNullValue());

            case "history":
            {
                IReadOnlyList<string> queries = await client.GetHistoryAsync(Int(req, "max", 50), ct);
                return Result(id, w =>
                {
                    w.WriteStartArray();
                    foreach (string q in queries)
                    {
                        w.WriteStringValue(q);
                    }

                    w.WriteEndArray();
                });
            }

            case "files":
            {
                FffNativeClient.FileMatch match = Str(req, "match") switch
                {
                    "plain" => FffNativeClient.FileMatch.Plain,
                    "glob" => FffNativeClient.FileMatch.Glob,
                    _ => FffNativeClient.FileMatch.Fuzzy,
                };
                FffNativeClient.FileSearch found =
                    await client.FindFilesAsync(query, match, Str(req, "currentFile"), max, ct);
                return Result(id, w =>
                {
                    w.WriteStartObject();
                    w.WriteStartArray("items");
                    foreach (FffNativeClient.FileItem f in found.Items)
                    {
                        WriteFile(w, f.Path, f.FrecencyScore, f.GitStatus, f.IsBinary, isDirectory: null);
                    }

                    w.WriteEndArray();
                    WriteLocation(w, found.Location);
                    w.WriteEndObject();
                });
            }

            case "mixed":
            {
                FffNativeClient.MixedSearch found = await client.FindMixedAsync(query, Str(req, "currentFile"), max, ct);
                return Result(id, w =>
                {
                    w.WriteStartObject();
                    w.WriteStartArray("items");
                    foreach (FffNativeClient.MixedItem m in found.Items)
                    {
                        WriteFile(w, m.Path, m.FrecencyScore, m.GitStatus, m.IsBinary, m.IsDirectory);
                    }

                    w.WriteEndArray();
                    WriteLocation(w, found.Location);
                    w.WriteEndObject();
                });
            }

            case "git":
            {
                IReadOnlyList<FffNativeClient.FileItem> files = await client.GitModifiedAsync(query, max, ct);
                return Result(id, w =>
                {
                    w.WriteStartArray();
                    foreach (FffNativeClient.FileItem f in files)
                    {
                        WriteFile(w, f.Path, f.FrecencyScore, f.GitStatus, f.IsBinary, isDirectory: null);
                    }

                    w.WriteEndArray();
                });
            }

            case "grep":
            {
                FffNativeClient.GrepMode mode = (Str(req, "mode") ?? "plain") switch
                {
                    "regex" => FffNativeClient.GrepMode.Regex,
                    "fuzzy" => FffNativeClient.GrepMode.Fuzzy,
                    "any" => FffNativeClient.GrepMode.Any,
                    _ => FffNativeClient.GrepMode.Plain,
                };
                FffNativeClient.GrepResult found = await client.GrepAsync(query, mode, max, ct);
                return Result(id, w =>
                {
                    w.WriteStartObject();
                    w.WriteStartArray("matches");
                    foreach (FffNativeClient.GrepMatch m in found.Matches)
                    {
                        w.WriteStartObject();
                        w.WriteString("path", m.Path);
                        w.WriteNumber("line", m.Line);
                        w.WriteNumber("col", m.Col);
                        w.WriteString("text", m.Text);
                        WriteRanges(w, "ranges", m.Ranges);
                        w.WriteString("gitStatus", m.GitStatus);
                        w.WriteBoolean("isBinary", m.IsBinary);
                        w.WriteBoolean("isDefinition", m.IsDefinition);
                        w.WriteEndObject();
                    }

                    w.WriteEndArray();
                    w.WriteString("regexError", found.RegexFallbackError);
                    w.WriteEndObject();
                });
            }

            case "symbols":
            {
                string dir = Str(req, "dir") ?? throw new ArgumentException("symbols needs dir");
                IReadOnlyList<SymbolIndex.Entry> entries = await SymbolIndex.GetAsync(client, dir, PostStatus, ct);
                List<SymbolIndex.Hit> hits = SymbolIndex.Query(entries, query, max);
                return Result(id, w =>
                {
                    w.WriteStartArray();
                    foreach (SymbolIndex.Hit h in hits)
                    {
                        w.WriteStartObject();
                        w.WriteString("path", h.Entry.Path);
                        w.WriteNumber("line", h.Entry.Line);
                        w.WriteNumber("col", h.Entry.Col);
                        w.WriteString("text", h.Entry.Text);
                        w.WriteString("kind", h.Entry.Kind);
                        w.WriteString("name", h.Entry.Name);
                        WriteRanges(w, "nameRanges", h.NameRanges);
                        w.WriteString("gitStatus", h.Entry.GitStatus);
                        w.WriteBoolean("isBinary", h.Entry.IsBinary);
                        w.WriteEndObject();
                    }

                    w.WriteEndArray();
                });
            }

            case "track":
                await client.TrackQueryAsync(query, Str(req, "path") ?? string.Empty, ct);
                return Result(id, w => w.WriteNullValue());

            default:
                throw new ArgumentException($"unknown op '{op}'");
        }
    }

    private void PostStatus(string message) =>
        // Called while FffNativeClient holds its gate: never wait for the write.
        _ = SendAsync(Write(w => w.WriteString("status", message)));

    private async Task SendAsync(byte[] line)
    {
        Stream? s = stream;
        if (s is null)
        {
            return;
        }

        await writeGate.WaitAsync();
        try
        {
            await s.WriteAsync(line);
            await s.FlushAsync();
        }
        catch (IOException)
        {
            // The client is gone; the read loop ends on its own.
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            writeGate.Release();
        }
    }

    private static byte[] Result(long id, Action<Utf8JsonWriter> writeValue) =>
        Write(w =>
        {
            w.WriteNumber("id", id);
            w.WritePropertyName("result");
            writeValue(w);
        });

    /// <summary>One line: an object whose members <paramref name="members"/> writes, then '\n'.</summary>
    private static byte[] Write(Action<Utf8JsonWriter> members)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            members(w);
            w.WriteEndObject();
        }

        buffer.Write("\n"u8);
        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteFile(Utf8JsonWriter w, string path, long frecency, string? gitStatus, bool isBinary, bool? isDirectory)
    {
        w.WriteStartObject();
        w.WriteString("path", path);
        w.WriteNumber("frecency", frecency);
        w.WriteString("gitStatus", gitStatus);
        w.WriteBoolean("isBinary", isBinary);
        if (isDirectory is bool dir)
        {
            w.WriteBoolean("isDirectory", dir);
        }

        w.WriteEndObject();
    }

    private static void WriteLocation(Utf8JsonWriter w, FffNativeClient.QueryLocation? location)
    {
        if (location is not FffNativeClient.QueryLocation loc)
        {
            w.WriteNull("location");
            return;
        }

        w.WriteStartObject("location");
        w.WriteNumber("line", loc.Line);
        if (loc.Col is int col)
        {
            w.WriteNumber("col", col);
        }
        else
        {
            w.WriteNull("col");
        }

        w.WriteEndObject();
    }

    private static void WriteRanges(Utf8JsonWriter w, string name, SeekyRange[] ranges)
    {
        w.WriteStartArray(name);
        foreach (SeekyRange r in ranges)
        {
            w.WriteStartArray();
            w.WriteNumberValue(r.Start);
            w.WriteNumberValue(r.End);
            w.WriteEndArray();
        }

        w.WriteEndArray();
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string name, int fallback) =>
        e.TryGetProperty(name, out JsonElement v) && v.TryGetInt32(out int n) ? n : fallback;

    public void Dispose()
    {
        client.Dispose();
        writeGate.Dispose();
    }
}
