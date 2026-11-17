using System;
using Microsoft.VisualStudio.Shell;
using VSNeo_Extension.Infrastructure;
using VSNeo_Extension.Nvim;

namespace VSNeo_Extension;

// Visual Studio actions nvim asks for: selections, ranges, focus, tabs, the picker.
public sealed partial class VSNeo_ExtensionPackage
{
    /// <summary>
    /// vsneo.cmd_select: run the command, then hand whatever it selected to
    /// nvim as visual mode (Edit.ExpandSelection / ContractSelection are
    /// semantic, and the selection they produce must become nvim's). RPC
    /// thread; same UI-thread hop as OnActionRequested.
    /// Skipped when nvim changed mode after asking: the hop is at Input, and
    /// a key behind it (v+d typed fast: d leaves visual, and its edit lands
    /// first at KeyResponse) made the hand-over drag nvim back into visual
    /// over text nobody selected. The mode-transition count is the test, read
    /// here on the RPC thread, in wire order.
    /// </summary>
    private void OnSelectActionRequested(string command)
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

        int asked = _session.State.ModeSequence;

#pragma warning disable VSTHRD001
        _ = dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Input,
            new Action(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (_session.State.ModeSequence != asked)
                {
                    Log.Write("select action " + command + ": nvim changed mode since - dropped");
                    return;
                }

                Execute(command, string.Empty);
                try
                { Editor.CursorSynchronizer.Instance?.HandOverSelection(); }
                catch (Exception ex) { Log.Write("selection hand-over after " + command + " failed", ex); }
            }));
#pragma warning restore VSTHRD001
    }

    /// <summary>
    /// An operator's range from vsneo.operator: select it in the active view,
    /// run the command over it, collapse the selection to the range's start
    /// (where Vim leaves the cursor after an operator). The command's edits
    /// come back through the mirror like any other Visual Studio edit.
    /// RPC thread. Two hops: the rows are pinned to the text at KeyResponse,
    /// the priority remote edits apply at, and the mirror's drain is held at
    /// this notification's place on the wire until they are - an edit nvim
    /// sent after the range (gccdd typed fast, a macro) used to land first,
    /// and the command ran over the rows that moved up: line 5 gone, the line
    /// below it commented. The command itself runs at Input, never at
    /// KeyResponse, over wherever the pinned text has gone - and not at all
    /// when that text has since been deleted: an empty selection would
    /// comment whatever line now sits there.
    /// </summary>
    private void OnRangeActionRequested(NvimSession.RangeAction action)
    {
        if (string.IsNullOrEmpty(action.Command))
        {
            return;
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            return;
        }

        Editor.BufferMirror.HoldDrainAfter(_session.NotificationSeq);
#pragma warning disable VSTHRD001
        _ = dispatcher.BeginInvoke(UiPriority.KeyResponse, new Action(() =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var view = Editor.CursorSynchronizer.Instance?.ActiveView;
            Microsoft.VisualStudio.Text.ITrackingSpan? range = null;
            bool hadText = false;
            try
            {
                if (view != null && !view.IsClosed)
                {
                    range = TrackRange(view.TextBuffer.CurrentSnapshot, action);
                    hadText = !range.GetSpan(view.TextBuffer.CurrentSnapshot).IsEmpty;
                }
            }
            catch (Exception ex) { Log.Write("range action " + action.Command + ": no range", ex); }
            finally { Editor.BufferMirror.ReleaseDrainHold(); }

            if (view == null || range == null)
            {
                return;
            }

            _ = dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                ExecuteOnRange(view, range, hadText, action.Command);
            }));
        }));
#pragma warning restore VSTHRD001
    }

    /// <summary>
    /// nvim's range - 1-based rows, byte columns, end inclusive - as a span of
    /// the snapshot, edge-exclusive so text typed at its edges stays out.
    /// </summary>
    private static Microsoft.VisualStudio.Text.ITrackingSpan TrackRange(
        Microsoft.VisualStudio.Text.ITextSnapshot snapshot, NvimSession.RangeAction action)
    {
        int lastLine = snapshot.LineCount - 1;
        var first = snapshot.GetLineFromLineNumber(Math.Max(0, Math.Min(action.StartRow - 1, lastLine)));
        var last = snapshot.GetLineFromLineNumber(Math.Max(first.LineNumber, Math.Min(action.EndRow - 1, lastLine)));

        int start, end;
        if (action.Kind == "line")
        {
            start = first.Start.Position;
            end = last.End.Position;
        }
        else
        {
            start = first.Start.Position + Math.Min(ColumnMapper.ByteToChar(first, Math.Max(action.StartByte, 0)), first.Length);
            // The inclusive end, stepped past its whole character: one UTF-16
            // unit split an emoji's surrogate pair.
            int c = Math.Min(ColumnMapper.ByteToChar(last, Math.Max(action.EndByte, 0)), last.Length);
            if (c < last.Length)
            {
                c += char.IsHighSurrogate(snapshot[last.Start.Position + c]) ? 2 : 1;
            }

            end = Math.Max(start, last.Start.Position + Math.Min(c, last.Length));
        }

        return snapshot.CreateTrackingSpan(start, end - start, Microsoft.VisualStudio.Text.SpanTrackingMode.EdgeExclusive);
    }

    private void ExecuteOnRange(Microsoft.VisualStudio.Text.Editor.IWpfTextView view,
                                Microsoft.VisualStudio.Text.ITrackingSpan range, bool hadText, string command)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            // The command runs on the focused view; one that lost focus since
            // would comment or format the wrong document.
            if (view.IsClosed || !ReferenceEquals(Editor.CursorSynchronizer.Instance?.ActiveView, view))
            {
                Log.Write("range action " + command + ": the view changed - dropped");
                return;
            }

            // The selection and its collapse are Visual Studio's bookkeeping,
            // not motions: nvim must not hear them. It already sits where the
            // operator left it, or has moved on (a macro, a quick .j) - and
            // the caret goes back to wherever nvim last said it is.
            // gcc on an empty line is a valid operator; a range that had text
            // and has none now was deleted since (gccdd typed fast).
            if (hadText && range.GetSpan(view.TextBuffer.CurrentSnapshot).IsEmpty)
            {
                Log.Write("range action " + command + ": its text was deleted since - dropped");
                return;
            }

            void Run()
            {
                var span = range.GetSpan(view.TextBuffer.CurrentSnapshot);
                view.Selection.Select(span, isReversed: false);
                Execute(command, string.Empty);

                // Collapse at the start, wherever it has gone since.
                var start = range.GetStartPoint(view.TextBuffer.CurrentSnapshot);
                view.Selection.Clear();
                view.Caret.MoveTo(start);
            }

            var sync = Editor.CursorSynchronizer.Instance;
            if (sync != null)
            {
                sync.WithoutEcho(Run);
            }
            else
            {
                Run();
            }
        }
        catch (Exception ex)
        {
            Log.Write("range action " + command + " failed", ex);
        }
    }

    /// <summary>
    /// Moves editor focus to the tab group adjacent to the active one, asked for by
    /// a Ctrl-W h/j/k/l mapping in nvim. Called on the RPC read thread.
    ///
    /// Same dispatcher-at-Input choice as OnActionRequested, for the same measured
    /// reason: this is the direct response to a keystroke and must not queue behind
    /// Visual Studio's background work.
    /// </summary>
    private void OnFocusRequested(string direction)
    {
        if (string.IsNullOrEmpty(direction))
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
                Editor.SplitNavigator.MoveFocus(direction, _uiShell, _monitorSelection);
            }));
#pragma warning restore VSTHRD001
    }

    // The lambdas run inside PostAtInput's dispatcher callback, which asserts
    // the UI thread; the analyzer cannot see through the indirection.
#pragma warning disable VSTHRD010
    private void OnMruRequested() =>
        PostAtInput(() => Editor.SplitNavigator.CycleBack(_monitorSelection));

    private void OnTabJumpRequested() =>
        PostAtInput(() => Editor.TabJumper.Begin(_session, _uiShell));

    private void OnTabJumpPicked(string label) =>
        PostAtInput(() => Editor.TabJumper.Pick(label));
#pragma warning restore VSTHRD010

    /// <summary>
    /// Shows the embedded Seeky picker, asked for by a vsneo.seeky call in nvim.
    /// Same Input-priority hop as every other keystroke-originated handler.
    /// Guarded like SeekyCommands.Execute: a picker that fails to open (a
    /// WebView2 load failure) logs instead of reaching the dispatcher unhandled.
    /// </summary>
    private void OnSeekyRequested(string mode, string query)
    {
        if (string.IsNullOrEmpty(mode))
        {
            return;
        }

        PostAtInput(() => _ = JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                // The cached catalog shows at once; the refresh runs behind it for
                // the next open (shortcuts rebound since). Only a first open, with
                // nothing cached yet, waits for the walk.
                if (mode == "vscommands")
                {
                    if (_vsCommands.Length == 0)
                    {
                        await RefreshVsCommandsAsync();
                    }
                    else
                    {
                        _ = JoinableTaskFactory.RunAsync(RefreshVsCommandsAsync);
                    }
                }

                await JoinableTaskFactory.SwitchToMainThreadAsync();
                VSNeo.Seeky.SeekyPickerController.Show(mode, query);
            }
            catch (Exception ex)
            {
                VSNeo.Seeky.SeekyLog.Error($"Seeky request '{mode}' failed", ex);
            }
        }));
    }
}
