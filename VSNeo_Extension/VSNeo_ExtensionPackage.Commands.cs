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

// Running Visual Studio commands, and the catalog the picker searches.
public sealed partial class VSNeo_ExtensionPackage
{
    /// <summary>
    /// Runs a Visual Studio command that a Vim mapping asked for. Called on the RPC
    /// read thread.
    ///
    /// Posted through the view's dispatcher at Input priority rather than
    /// SwitchToMainThreadAsync: this is the direct response to a keystroke, and the
    /// JoinableTask route was measured queueing behind Visual Studio's background
    /// work at 373ms average. gd should feel like a key press, not a request.
    /// </summary>
    private void OnActionRequested(string command, string args)
    {
        if (string.IsNullOrEmpty(command))
        {
            return;
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            return;
        }

#pragma warning disable VSTHRD001
        _ = dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Input,
            new Action(() =>
            {
                // The dispatcher guarantees the main thread here, but the analyzer
                // cannot prove it through a BeginInvoke callback.
                ThreadHelper.ThrowIfNotOnUIThread();
                Execute(command, args);
            }));
#pragma warning restore VSTHRD001
    }

    private Infrastructure.VsCommandInfo[] _vsCommands = Array.Empty<Infrastructure.VsCommandInfo>();
    private Task? _vsCommandsWalk;   // UI thread only

    /// <summary>
    /// One walk at a time, and a caller arriving mid-walk waits for that walk:
    /// a first :Vsclist during the startup walk used to return at once and
    /// show an empty picker.
    /// </summary>
    private async Task RefreshVsCommandsAsync()
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync();
        if (_vsCommandsWalk == null || _vsCommandsWalk.IsCompleted)
        {
            _vsCommandsWalk = WalkVsCommandsAsync();
        }

        await _vsCommandsWalk;
    }

    /// <summary>
    /// Enumerates DTE on the UI thread; only immutable snapshots cross to nvim.
    /// Three COM calls per command, several thousand commands: like
    /// KeyBindingCleaner's walk, it yields to the message pump every 250, or it
    /// froze the UI at startup (beside the cleaner) and on every :Vsclist. The
    /// push to nvim is not awaited.
    /// </summary>
    private async Task WalkVsCommandsAsync()
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync();
        var session = _session;
        if (session == null || !session.IsReady || _dte == null)
        {
            return;
        }

        Infrastructure.VsCommandInfo[] commands;
        try
        {
            var read = new System.Collections.Generic.List<Infrastructure.VsCommandInfo>();
            int walked = 0;
            foreach (EnvDTE.Command command in _dte.Commands)
            {
                if (++walked % 250 == 0)
                {
                    await Task.Yield();
                    await JoinableTaskFactory.SwitchToMainThreadAsync();
                }

                var name = command.Name;
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                string displayName;
                try
                { displayName = command.LocalizedName ?? string.Empty; }
                catch { displayName = string.Empty; }

                read.Add(new Infrastructure.VsCommandInfo(name, displayName, ReadBindings(command.Bindings)));
            }

            commands = Infrastructure.VsCommandCatalog.Sort(read);
            _vsCommands = commands;
        }
        catch (Exception ex)
        {
            Log.Write("Visual Studio command enumeration failed", ex);
            return;
        }

        var names = new string[commands.Length];
        for (int i = 0; i < commands.Length; i++)
        {
            names[i] = commands[i].Name;
        }

        _ = session.RequestAsync("nvim_exec_lua", "vsneo._set_vs_commands(...)", new object[] { names })
            .ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    private static string ReadBindings(object value)
    {
        if (!(value is System.Collections.IEnumerable values) || value is string)
        {
            return string.Empty;
        }

        var result = new System.Text.StringBuilder();
        foreach (var item in values)
        {
            if (!(item is string binding) || binding.Length == 0)
            {
                continue;
            }

            if (result.Length > 0)
            {
                result.Append(", ");
            }

            result.Append(binding);
        }

        return result.ToString();
    }

    internal static System.Collections.Generic.IReadOnlyList<(string Name, string DisplayName, string Bindings)>
        GetVsCommands()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var package = s_instance;
        if (package == null)
        {
            return Array.Empty<(string, string, string)>();
        }

        var commands = package._vsCommands;
        var result = new (string Name, string DisplayName, string Bindings)[commands.Length];
        for (int i = 0; i < commands.Length; i++)
        {
            result[i] = (commands[i].Name, commands[i].DisplayName, commands[i].Bindings);
        }

        return result;
    }

    /// <summary>
    /// Runs a Visual Studio command for code that is not the package and has no
    /// RPC event to ride - TextViewCreationListener following nvim to a file.
    /// Any thread; the hop is OnActionRequested's, with its measured
    /// Input-priority reasoning.
    /// </summary>
    internal static void RunVsCommand(string command, string args)
    {
        var package = s_instance;
        if (package == null)
        {
            return;
        }
#pragma warning disable VSTHRD010
        // The analyzer sees a package method and wants the main thread, but
        // OnActionRequested's whole job is to be callable from the RPC read
        // thread: it does the Input-priority dispatcher hop itself.
        package.OnActionRequested(command, args);
#pragma warning restore VSTHRD010
    }

    private void Execute(string command, string args)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        // Some commands misbehave on DTE's global route: the C++ language
        // service answers Edit.GoToDefinition there with a modal "Command
        // requires one argument" - outside the editor's context it wants the
        // symbol spelled out. The active view's IOleCommandTarget is the
        // route F12 itself takes, so those commands go there first.
        if (EditorRouted.TryGetValue(command, out var cmdId) && TryExecOnActiveView(cmdId))
        {
            return;
        }

        try
        {
            var dte = _dte;
            if (dte == null)
            {
                Infrastructure.Log.Write("cannot run " + command + ": no DTE yet");
                return;
            }

            dte.ExecuteCommand(command, args ?? string.Empty);
        }
        catch (Exception ex)
        {
            // A command that is unavailable in the current context throws, and that
            // is ordinary - Go To Definition on a comment, say. Worth a line, not a
            // fault: the mapping should simply do nothing.
            Infrastructure.Log.Write("VS command \"" + command + "\" did not run", ex);
        }
    }

    // Case-insensitive: a VsVim-ported rc writes Edit.GotoDefinition.
    private static readonly System.Collections.Generic.Dictionary<string, VSConstants.VSStd97CmdID>
        EditorRouted = new System.Collections.Generic.Dictionary<string, VSConstants.VSStd97CmdID>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["Edit.GoToDefinition"] = VSConstants.VSStd97CmdID.GotoDefn,
            ["Edit.GoToDeclaration"] = VSConstants.VSStd97CmdID.GotoDecl,
        };

    private static bool TryExecOnActiveView(VSConstants.VSStd97CmdID id)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            if (GetGlobalService(typeof(SVsTextManager)) is IVsTextManager mgr
                && mgr.GetActiveView(1, null, out IVsTextView view) == VSConstants.S_OK
                && view is IOleCommandTarget target)
            {
                var group = VSConstants.GUID_VSStandardCommandSet97;
                return target.Exec(ref group, (uint)id,
                    (uint)OLECMDEXECOPT.OLECMDEXECOPT_DODEFAULT,
                    IntPtr.Zero, IntPtr.Zero) == VSConstants.S_OK;
            }
        }
        catch (Exception ex)
        {
            Infrastructure.Log.Write("editor-routed command " + id + " failed", ex);
        }

        return false;
    }
}
