using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using VSNeo_Extension.Infrastructure;
using VSNeo_Extension.Nvim;

namespace VSNeo_Extension;

// Starting, restarting and reporting on the Neovim session.
public sealed partial class VSNeo_ExtensionPackage
{
    /// <summary>
    /// Options &gt; Neovim path, then VSNEO_NVIM_PATH, then nvim.exe on PATH.
    /// </summary>
    internal static string ResolveNvimPath()
    {
        string configured = VSNeoSettings.NeovimPath;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim();
        }

        string? environment = Environment.GetEnvironmentVariable("VSNEO_NVIM_PATH");
        return string.IsNullOrWhiteSpace(environment) ? "nvim.exe" : environment.Trim();
    }

    /// <summary>
    /// Tools &gt; VSNeo: Restart Neovim. Retry exists for the startup
    /// failure - a missing or wrong nvim path - which is the one case where
    /// nothing is attached yet and a fresh session is safe. After a session
    /// that went ready and then died, document mirrors still point at it;
    /// replacing the session under them would leave every mirror writing to a
    /// dead client, so that case asks for a Visual Studio restart instead.
    /// </summary>
    internal static void RestartNeovim()
    {
        var package = s_instance;
        if (package != null)
        {
            _ = package.RestartSessionAsync();
        }
    }

    private async Task RestartSessionAsync()
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync();

        if (Session is { IsReady: true })
        {
            await SetStatusAsync("VSNeo: Neovim is running; restart Visual Studio to replace it.");
            return;
        }

        if (Editor.TextViewCreationListener.AnyMirrorAttached)
        {
            await SetStatusAsync("VSNeo: documents were already attached; restart Visual Studio to reconnect.");
            return;
        }

        var old = _session;
        Session = null!;
        if (old != null)
        {
            // Its start may still be in flight; that start ends by announcing
            // "not ready", which would land on the replacement: fallback text,
            // the mode badge and the command-line overlay detached.
            old.ReadyChanged -= OnReadyChanged;
            old.Dispose();
        }

        // The failed attempt may have opened the breaker. A retry starts a
        // clean count, so one bad path does not keep it tripped after a fix.
        Breaker.Reset();

        var session = new NvimSession(Breaker);
        WireSession(session);
        _session = session;
        Session = session;

        string path = ResolveNvimPath();
        Log.Write("restarting nvim: " + path);
        await SetStatusAsync("VSNeo: starting Neovim...");
        _ = JoinableTaskFactory.RunAsync(() => session.StartAsync(path, _lifetime.Token));
    }

    private async Task SetStatusAsync(string text)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync();
        if (await GetServiceAsync(typeof(SVsStatusbar)) is IVsStatusbar bar)
        {
            bar.SetText(text);
        }
    }

    /// <summary>Tools &gt; VSNeo: Diagnostics: the same report :VSNeoInfo shows.</summary>
    internal static void ShowDiagnostics()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var package = s_instance;
        if (package == null)
        {
            return;
        }

        VsShellUtilities.ShowMessageBox(
            package,
            Editor.InfoRequest.Build(Session),
            "VSNeo diagnostics",
            OLEMSGICON.OLEMSGICON_INFO,
            OLEMSGBUTTON.OLEMSGBUTTON_OK,
            OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
    }

    /// <summary>
    /// Says so in the status bar when a document's mirror gives up. Degrading in
    /// silence is how someone keeps pressing dd into a file that stopped listening
    /// several minutes ago.
    /// </summary>
    private void OnMirrorStopped(string filePath)
    {
        _ = JoinableTaskFactory.RunAsync(async () =>
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            if (await GetServiceAsync(typeof(SVsStatusbar)) is IVsStatusbar bar)
            {
                bar.SetText("VSNeo: stopped syncing "
                            + System.IO.Path.GetFileName(filePath ?? "this document")
                            + " - reopen it to resume");
            }
        });
    }
}
