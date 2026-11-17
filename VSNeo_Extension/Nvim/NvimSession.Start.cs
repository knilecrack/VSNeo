using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading; // TaskScheduler.GetAwaiter, for "await TaskScheduler.Default"
using VSNeo_Extension.Infrastructure;

namespace VSNeo_Extension.Nvim;

// Startup: the process, the handshake, the version gate and the failure text.
internal sealed partial class NvimSession
{
    public async Task StartAsync(string nvimPath, string? configDirectory, CancellationToken ct)
    {
        await TaskScheduler.Default; // never start this on the UI thread

        NvimPath = nvimPath;
        ConfigDirectory = NvimLaunch.ConfigDirectory(configDirectory, Environment.GetEnvironmentVariable);
        NvimVersion = null;
        StartupFailure = null;

        NvimRpcClient? client = null;
        Volatile.Write(ref _faultedBeforeReady, 0);
        try
        {
            Log.Write("starting nvim: " + nvimPath + ", config " + ConfigDirectory);
            client = await NvimRpcClient.ConnectAsync(nvimPath, configDirectory, ct).ConfigureAwait(false);
            Log.Write("pipe connected");

            // Subscribe before the read loop starts, or the first redraw - the
            // one carrying the initial mode - can land before anyone is listening.
            client.NotificationReceived += State.OnNotification;
            client.StatePushReceived += State.OnStatePush;
            client.NotificationReceived += OnNotification;
            client.Faulted += OnClientFaulted;
            client.RequestHandler = (method, args) =>
                RequestHandler?.Invoke(method, args)
                ?? throw new NotSupportedException("no handler for " + method);
            client.BeginRead();

            // ext_linegrid is required for the modern redraw protocol.
            // ext_cmdline and ext_messages hand us the ":" prompt and Vim's
            // own messages so we never have to reimplement either.
            // ext_popupmenu reports wildmenu (cmdline Tab-completion) as
            // events instead of drawing it, so the popup can render it.
            var options = new Dictionary<string, object>
            {
                ["ext_linegrid"] = true,
                ["ext_cmdline"] = true,
                ["ext_messages"] = true,
                ["ext_popupmenu"] = true,
                ["rgb"] = true,
            };

            await client.RequestAsync("nvim_ui_attach", StartupRequestTimeout, 200, 60, options).ConfigureAwait(false);
            await client.RequestAsync("nvim_set_var", StartupRequestTimeout, "vsneo", 1).ConfigureAwait(false);
            // nvim_get_api_info returns [channel_id, metadata]: the id is how the
            // companion addresses its notifications back to this connection.
            var apiInfo = await client.RequestAsync("nvim_get_api_info", StartupRequestTimeout).ConfigureAwait(false) as object[];
            if (apiInfo == null || apiInfo.Length < 1)
            {
                throw new InvalidOperationException("nvim_get_api_info returned nothing usable");
            }

            Version? version = apiInfo.Length > 1
                ? ParseVersion(apiInfo[1] as IDictionary<string, object?>)
                : null;
            if (version == null)
            {
                // A fork that omits the version map still gets to run; the
                // failure modes below say what actually broke.
                Log.Write("nvim version missing from nvim_get_api_info; continuing");
            }
            else
            {
                NvimVersion = version.ToString(3);
                if (version < MinNvimVersion)
                {
                    throw new InvalidOperationException(
                        "Neovim " + MinNvimVersion.ToString(2) + " or newer is required; "
                        + nvimPath + " reports " + NvimVersion + ".");
                }
            }

            long channel = Convert.ToInt64(apiInfo[0]);
            object? companion = await client.RequestAsync(
                "nvim_exec_lua", CompanionTimeout, NvimLua.Script, new object[] { channel })
                .ConfigureAwait(false);
            if (!TryToInt(companion, out int protocol) || protocol != NvimProtocol.Version)
            {
                throw new InvalidOperationException(
                    "VSNeo companion protocol mismatch (script "
                    + (protocol == 0 ? "unknown" : protocol.ToString())
                    + ", extension " + NvimProtocol.Version
                    + "). Reinstall the extension so vsneo.lua matches the DLL.");
            }

            Log.Write("state companion installed on channel " + channel
                      + " (protocol " + protocol + ")");
        }
        catch (Exception ex)
        {
            StartupFailure = DescribeFailure(ex, nvimPath);
            if (ex is TimeoutException)
            {
                StartupFailure += " Config directory: " + ConfigDirectory + ".";
            }

            Log.Write("nvim start FAILED: " + StartupFailure, ex);

            // A failure after the pipe connected (ui_attach timing out, the
            // companion refusing to load) used to leave the client
            // unowned: nvim, its pipe, the read loop and the job handle all
            // lived until devenv exited, and the handlers subscribed above
            // kept feeding a dead session's notifications into the hub.
            // Only the client that never became _client is ours to close.
            if (client != null && !ReferenceEquals(client, _client))
            {
                try
                { client.Dispose(); }
                catch (Exception dex) { Log.Write("disposing failed nvim client", dex); }
            }

            _breaker.Trip(ex);

            // Trip only opens the breaker on the third failure, so a single
            // startup failure is otherwise completely silent: no ready event,
            // no status bar text, and every key quietly passing through to VS
            // with nothing anywhere to say why. Announce it directly.
            NotifyReady(false);
            return;
        }

        // Outside the try. A ReadyChanged subscriber that threw used to land
        // in the catch above, which announced a failed start over a session
        // that was live (_ready set, _client assigned): the badge said
        // fallback while keys kept going to nvim, and every subscriber after
        // the throwing one never heard that the session was ready.
        var started = client!;
        if (Volatile.Read(ref _disposedFlag) != 0 || Volatile.Read(ref _faultedBeforeReady) != 0)
        {
            Log.Write(Volatile.Read(ref _disposedFlag) != 0
                ? "nvim started after the session was disposed - retiring it"
                : "nvim transport faulted during startup - not going ready");
            try
            { started.Dispose(); }
            catch (Exception dex) { Log.Write("disposing the client", dex); }

            NotifyReady(false);
            return;
        }

        _client = started;
        Interlocked.Exchange(ref _ready, 1);

        // The check above is not atomic with the publication: Dispose or a
        // transport fault can land between the two. Publish first, then look
        // again (the Interlocked calls are full fences): whichever side wrote
        // last sees the other's write, so a disposed or faulted client is
        // never left standing as ready.
        if (Interlocked.CompareExchange(ref _disposedFlag, 0, 0) != 0
            || Interlocked.CompareExchange(ref _faultedBeforeReady, 0, 0) != 0)
        {
            Log.Write("nvim session was disposed or faulted while going ready - retiring it");
            Interlocked.Exchange(ref _ready, 0);
            _client = null;
            try
            { started.Dispose(); }
            catch (Exception dex) { Log.Write("disposing the client", dex); }

            NotifyReady(false);
            return;
        }

        _breaker.Reset();
        Log.Write("nvim connected and ui_attach succeeded");
        StartTrafficStats(started);
        NotifyReady(true);
    }

    /// <summary>
    /// Samples RPC volume every five seconds and logs it only when there is
    /// something to see. Idle traffic should be zero: nothing in this design
    /// polls, so anything ticking over while the editor sits untouched is a
    /// feedback loop, and the rate says which side is driving it.
    /// </summary>
    private void StartTrafficStats(NvimRpcClient client)
    {
        long lastSent = 0, lastReceived = 0;

        _stats = new Timer(_ =>
        {
            long sent = client.Sent, received = client.Received;
            long dSent = sent - lastSent, dReceived = received - lastReceived;
            lastSent = sent;
            lastReceived = received;

            if (dSent == 0 && dReceived == 0)
            {
                return;
            }

            Log.Write(string.Format(
                "rpc/5s: sent {0}, received {1}   (totals {2}/{3})",
                dSent, dReceived, sent, received));
        }, null, 5000, 5000);
    }

    /// <summary>
    /// "version" from nvim_get_api_info's metadata: {major, minor, patch,
    /// prerelease, api_level, ...}. Null when the map is absent or useless.
    /// </summary>
    private static Version? ParseVersion(IDictionary<string, object?>? metadata)
    {
        if (metadata == null
            || !metadata.TryGetValue("version", out object? raw)
            || raw is not IDictionary<string, object?> version)
        {
            return null;
        }

        int Field(string key) =>
            version.TryGetValue(key, out object? value) ? Convert.ToInt32(value) : 0;

        int major = Field("major");
        int minor = Field("minor");
        if (major == 0 && minor == 0)
        {
            return null;
        }

        return new Version(major, minor, Math.Max(Field("patch"), 0));
    }

    private static bool TryToInt(object? value, out int result)
    {
        try
        {
            if (value != null)
            {
                result = Convert.ToInt32(value);
                return true;
            }
        }
        catch
        {
            // A malformed protocol value reports as unknown below.
        }

        result = 0;
        return false;
    }

    /// <summary>
    /// The friendly half of a startup failure: a missing nvim is the common
    /// one, and "the file could not be found" in the status bar does not say
    /// where to fix it.
    /// </summary>
    private static string DescribeFailure(Exception ex, string nvimPath)
    {
        if (ex is Win32Exception win32 && win32.NativeErrorCode == 2)
        {
            return "Neovim not found: " + nvimPath
                   + ". Set Tools > Options > VSNeo > Neovim path, or VSNEO_NVIM_PATH.";
        }

        if (ex is FileNotFoundException)
        {
            return "Neovim not found: " + nvimPath + ".";
        }

        return ex.Message;
    }
}
