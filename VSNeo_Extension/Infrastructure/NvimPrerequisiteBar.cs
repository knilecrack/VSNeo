using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace VSNeo_Extension.Infrastructure
{
    /// <summary>
    /// The InfoBar across the top of the main window when Neovim is missing
    /// or too old. The install-time equivalent a VSIX cannot have: the VSIX
    /// installer runs no extension code and its prerequisites can only name
    /// Visual Studio components, so the check runs at package load instead.
    ///
    /// Offers winget when the App Installer is present (a visible console, so
    /// the download progress and winget's own elevation prompt are seen), the
    /// release page otherwise, and a retry. After winget finishes the session
    /// is started again without restarting Visual Studio: the locator probes
    /// the install directory directly, since a PATH change made by the
    /// installer does not reach a process that is already running.
    /// </summary>
    internal sealed class NvimPrerequisiteBar : IVsInfoBarUIEvents
    {
        private const string InstallAction = "install";
        private const string DownloadAction = "download";
        private const string RetryAction = "retry";
        private const string ReleasesUrl = "https://github.com/neovim/neovim/releases/latest";

        private static NvimPrerequisiteBar? _current;

        private readonly AsyncPackage _package;
        private readonly Func<Task> _retry;
        private readonly bool _upgrade;
        private readonly string? _wingetPath;
        private IVsInfoBarUIElement? _element;
        private uint _cookie;

        private NvimPrerequisiteBar(AsyncPackage package, Func<Task> retry, bool upgrade, string? wingetPath)
        {
            _package = package;
            _retry = retry;
            _upgrade = upgrade;
            _wingetPath = wingetPath;
        }

        /// <summary>nvim.exe was not found anywhere the locator looks.</summary>
        public static Task ShowMissingAsync(AsyncPackage package, Func<Task> retry) =>
            ShowAsync(package, retry, upgrade: false,
                "VSNeo: Neovim (nvim.exe) was not found, so Vim keys are off and Visual Studio input is unchanged. "
                + "Install it, or point " + NvimLocator.PathVariable + " at it, then retry.");

        /// <summary>nvim answered, but is older than the companion script needs.</summary>
        public static Task ShowTooOldAsync(AsyncPackage package, Func<Task> retry, Version found) =>
            ShowAsync(package, retry, upgrade: true,
                "VSNeo: Neovim " + found + " was found, but " + NvimLocator.MinimumVersion
                + " or newer is required. Vim keys are off until it is updated.");

        private static async Task ShowAsync(AsyncPackage package, Func<Task> retry, bool upgrade, string text)
        {
            try
            {
                var wingetPath = await Task.Run(() => NvimLocator.FindWinget()).ConfigureAwait(false);
                await package.JoinableTaskFactory.SwitchToMainThreadAsync();
                _current?.Close();

                var bar = new NvimPrerequisiteBar(package, retry, upgrade, wingetPath);
                if (await bar.TryAttachAsync(text)) _current = bar;
                else Log.Write("prerequisite InfoBar could not be shown (no InfoBar host); see the log line above");
            }
            catch (Exception ex)
            {
                Log.Write("prerequisite InfoBar failed", ex);
            }
        }

        private async Task<bool> TryAttachAsync(string text)
        {
            var factory = await _package.GetServiceAsync(typeof(SVsInfoBarUIFactory)) as IVsInfoBarUIFactory;
            var shell = await _package.GetServiceAsync(typeof(SVsShell)) as IVsShell;
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (factory == null || shell == null) return false;

            if (ErrorHandler.Failed(shell.GetProperty((int)__VSSPROPID7.VSSPROPID_MainWindowInfoBarHost, out object hostObject))
                || !(hostObject is IVsInfoBarHost host))
                return false;

            var actions = new List<IVsInfoBarActionItem>();
            if (_wingetPath != null)
                actions.Add(new InfoBarButton(_upgrade ? "Update with winget" : "Install with winget", InstallAction));
            actions.Add(new InfoBarHyperlink("Download Neovim", DownloadAction));
            actions.Add(new InfoBarHyperlink("Retry", RetryAction));

            var model = new InfoBarModel(
                new[] { new InfoBarTextSpan(text) },
                actions,
                KnownMonikers.StatusWarning,
                isCloseButtonVisible: true);

            _element = factory.CreateInfoBar(model);
            _element.Advise(this, out _cookie);
            host.AddInfoBar(_element);
            return true;
        }

        public void OnClosed(IVsInfoBarUIElement infoBarUIElement)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try { infoBarUIElement.Unadvise(_cookie); } catch { }
            if (ReferenceEquals(_current, this)) _current = null;
            _element = null;
        }

        public void OnActionItemClicked(IVsInfoBarUIElement infoBarUIElement, IVsInfoBarActionItem actionItem)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            switch (actionItem.ActionContext as string)
            {
                case InstallAction:
                    RunWinget();
                    break;
                case DownloadAction:
                    OpenUrl(ReleasesUrl);
                    break;
                case RetryAction:
                    Close();
                    RetryLater();
                    break;
            }
        }

        private void Close()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try { _element?.Close(); } catch { }
        }

        private void RetryLater()
        {
            var retry = _retry;
#pragma warning disable VSSDK007
            _ = _package.JoinableTaskFactory.RunAsync(async () =>
            {
                try { await retry(); }
                catch (Exception ex) { Log.Write("nvim retry failed", ex); }
            });
#pragma warning restore VSSDK007
        }

        /// <summary>
        /// winget in a visible console. The machine-scope package elevates
        /// through winget's own prompt; the console is where that and the
        /// download progress appear. When it exits, the session is retried.
        /// </summary>
        private void RunWinget()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_wingetPath == null)
            {
                OpenUrl(ReleasesUrl);
                return;
            }

            string verb = _upgrade ? "upgrade" : "install";
            string arguments = verb + " --id " + NvimLocator.WingetId
                + " -e --accept-source-agreements --accept-package-agreements";
            Log.Write("running: " + _wingetPath + " " + arguments);

            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo(_wingetPath, arguments)
                    {
                        UseShellExecute = true,
                    },
                    EnableRaisingEvents = true,
                };
                process.Exited += (s, e) =>
                {
                    int code = -1;
                    try { code = process.ExitCode; } catch { }
                    process.Dispose();
                    Log.Write("winget exited with " + code + "; looking for Neovim again");
#pragma warning disable VSSDK007
                    _ = _package.JoinableTaskFactory.RunAsync(async () =>
                    {
                        await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                        Close();
                        try { await _retry(); }
                        catch (Exception ex) { Log.Write("nvim retry after winget failed", ex); }
                    });
#pragma warning restore VSSDK007
                };
                process.Start();
            }
            catch (Exception ex)
            {
                Log.Write("could not start winget", ex);
                OpenUrl(ReleasesUrl);
            }
        }

        private static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Write("could not open " + url, ex); }
        }
    }
}
