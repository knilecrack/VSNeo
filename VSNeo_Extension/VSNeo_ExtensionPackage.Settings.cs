using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;
using VSNeo_Extension.Infrastructure;
using VSNeo_Extension.Nvim;

namespace VSNeo_Extension;

// The enable switch, and the key bindings that follow it.
public sealed partial class VSNeo_ExtensionPackage
{
    // The Enabled value the key bindings were last set up for; null until the
    // startup pass. UI thread only, like _bindingsBusy.
    private bool? _bindingsFor;
    private bool _bindingsBusy;

    /// <summary>
    /// Enabled, the Vim chords are taken from Visual Studio (KeyBindingCleaner);
    /// disabled, what was taken is put back, so the switch really leaves plain
    /// Visual Studio. Once per state: the startup pass, then each flip.
    /// One pass at a time: both yield every 250 commands, and a flip during
    /// the startup walk restored everything while the walk went on unbinding
    /// the commands it had not reached. A flip while a pass runs is picked up
    /// when it ends.
    /// </summary>
    private async Task ApplyKeyBindingsAsync(EnvDTE.DTE dte)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync();
        VisualStudioVersion ??= dte.Version;
        if (_bindingsBusy)
        {
            return;
        }

        _bindingsBusy = true;
        try
        {
            while (_bindingsFor != VSNeoSettings.Enabled)
            {
                bool enabled = VSNeoSettings.Enabled;
                _bindingsFor = enabled;
                if (enabled)
                {
                    await Infrastructure.KeyBindingCleaner.RunAsync(dte);
                }
                else
                {
                    await Infrastructure.KeyBindingCleaner.RestoreAsync(dte);
                }
            }
        }
        finally
        {
            _bindingsBusy = false;
        }
    }

#pragma warning disable VSTHRD010
    private void OnToggleRequested(string state) => PostAtInput(() =>
    {
        if (state == "on")
        {
            SetEnabled(true);
        }
        else if (state == "off")
        {
            SetEnabled(false);
        }
        else
        {
            ToggleEnabled();
        }
    });
#pragma warning restore VSTHRD010

    internal static void ToggleEnabled()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        SetEnabled(!VSNeoSettings.Enabled);
    }

    internal static void SetEnabled(bool enabled)
    {
        var package = s_instance;
        if (package == null)
        {
            return;
        }

        ThreadHelper.ThrowIfNotOnUIThread();
        if (package.GetDialogPage(typeof(VSNeoOptionsPage)) is VSNeoOptionsPage options)
        {
            options.Enabled = enabled;
            VSNeoSettings.SetEnabled(enabled);
            options.SaveSettingsToStorage();
        }
        else
        {
            VSNeoSettings.SetEnabled(enabled);
        }
    }

    private void OnSettingsChanged()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var session = _session;
        bool ready = session != null && session.IsReady;
        if (session != null && session.IsReady)
        {
            Editor.ModeStatusBarItem.Attach(session);
            if (VSNeoSettings.Enabled)
            {
                Editor.CmdLineOverlayWindow.Attach(session);
            }
            else
            {
                Editor.CmdLineOverlayWindow.Hide();
                session.State.ClosePager();
            }
        }
        else
        {
            Editor.ModeStatusBarItem.Hide();
        }

        // Before the startup pass has run, it will pick up the current state itself.
        var dte = _dte;
        if (dte != null && _bindingsFor.HasValue && _bindingsFor != VSNeoSettings.Enabled)
        {
            _ = JoinableTaskFactory.RunAsync(() => ApplyKeyBindingsAsync(dte));
        }

        _ = JoinableTaskFactory.RunAsync(async () =>
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            if (await GetServiceAsync(typeof(SVsStatusbar)) is IVsStatusbar bar)
            {
                bar.SetText(!ready
                    ? (_session?.StartupFailure is string failure
                        ? "VSNeo: " + failure
                        : "VSNeo: fallback (VS input)")
                    : VSNeoSettings.Enabled ? "VSNeo: connected" : "VSNeo: disabled (VS input)");
            }
        });
    }
}
