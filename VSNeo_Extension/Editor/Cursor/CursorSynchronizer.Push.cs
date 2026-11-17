using System.Threading;
using Microsoft.VisualStudio.Shell;
using VSNeo_Extension.Infrastructure;
using VSNeo_Extension.Nvim;


namespace VSNeo_Extension.Editor;

// The VS caret back to nvim, with the echo bookkeeping that keeps it from bouncing.
internal sealed partial class CursorSynchronizer
{
    /// <summary>
    /// Push the active view's caret into nvim. UI thread.
    ///
    /// <paramref name="force"/> bypasses the dedupe, for the one case where it
    /// would do harm: leaving insert mode. Escape makes nvim move the cursor one
    /// column left, and it computes that from its own copy - which, after a burst
    /// of typing VS handled alone, may be a character behind. The dedupe would
    /// suppress the correcting push precisely because we "already sent" that
    /// position, even though nvim clamped it on arrival and is somewhere else.
    /// </summary>
    public void SyncCaretToNvim(bool force = false)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var view = _activeView;
        if (view == null || view.IsClosed)
        {
            return;
        }
        // Replace mode typed through nvim, so the caret is the one lagging.
        if (VSNeo_ExtensionPackage.Session?.State.Mode == VimMode.Replace)
        {
            return;
        }

        PushCaret(view.Caret.Position.BufferPosition, force);
    }

    /// <summary>
    /// A claimed insert-mode mapping may move nvim's cursor without
    /// leaving insert (imap &lt;C-l&gt; &lt;Right&gt;). Allow the next push
    /// through the Visual-Studio-owns-the-caret rule - the same exception
    /// the move into insert gets. One shot: the flag is consumed by the
    /// next apply either way, so a mapping that leaves insert (where the
    /// mode change sets it itself) is unaffected.
    /// </summary>
    public void AllowNextInsertApply() => Volatile.Write(ref _applyOnceInInsert, 1);

    private void PushCaret(Microsoft.VisualStudio.Text.SnapshotPoint point, bool force = false)
    {
        var session = VSNeo_ExtensionPackage.Session;
        if (session == null || !session.IsReady || !VSNeoSettings.Enabled)
        {
            return;
        }

        // A mouse drag moves the caret continuously; the mouse-up handler
        // sends the finished selection (or the clicked position), so the
        // frames along the way are noise - and each echoed redraw would be
        // due for suppression in ApplyPending anyway.
        if (MouseIsDown)
        {
            return;
        }

        // Moving nvim's cursor mid-command-line would disturb the prompt.
        if (session.State.Mode == VimMode.CmdLine)
        {
            return;
        }

        var line = point.GetContainingLine();
        int byteColumn = ColumnMapper.CharToByte(
            line, point.Position - line.Start.Position);

        // PositionChanged fires for layouts, scrolls and word wrap, not only for
        // real cursor movement, and it fires for the caret landing back where it
        // already was. Without this every scroll became a burst of RPC that nvim
        // answered with a redraw, which moved the caret, which fired again.
        long packed = ((long)line.LineNumber << 32) | (uint)byteColumn;
        if (Interlocked.Exchange(ref _lastPushed, packed) == packed && !force)
        {
            return;
        }

        // Routed through the companion rather than raw nvim_win_set_cursor:
        // the API scrolls nvim's window to reveal the cursor when the pushed
        // caret sits outside it, and the WinScrolled report of that
        // transitional scroll used to reach us before note_viewport's
        // winrestview with the real topline - the view yanked to nvim's
        // minimal scroll on every VS-initiated jump (gd, Ctrl+-, a far
        // click), and the correction was then swallowed by the echo ring.
        // vsneo.set_cursor moves the cursor and reports the position, but
        // marks the push as carrying no scroll information. Same convention
        // as nvim_win_set_cursor: 1-based row, 0-based byte column.
        var pos = new object[] { line.LineNumber + 1, byteColumn };
        Observe(session.RequestAsync("nvim_exec_lua", "vsneo.set_cursor(...)", pos));
    }

    /// <summary>
    /// The mirror is one-way and can lag, so nvim will occasionally reject a
    /// position as out of range. That is expected and self-correcting on the
    /// next redraw; what we must not do is leave the task unobserved.
    /// </summary>
    private static int s_pushFailures;
}
