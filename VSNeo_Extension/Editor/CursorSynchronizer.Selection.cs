using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Input;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;
using VSNeo_Extension.Infrastructure;
using VSNeo_Extension.Nvim;

namespace VSNeo_Extension.Editor;

// Visual and selection synchronization: nvim selection -> VS, VS selection -> nvim.
internal sealed partial class CursorSynchronizer
{
    /// <summary>
    /// UI thread. A Visual Studio command changed the selection
    /// (Edit.ExpandSelection, Edit.ContractSelection): hand it to nvim as
    /// a visual selection, the way a mouse drag is handed over. An empty
    /// selection leaves visual mode, as a click does.
    /// </summary>
    public void HandOverSelection()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        // Disabled, the selection is plain Visual Studio's, as for a mouse drag.
        var view = _activeView;
        if (view == null || view.IsClosed || !VSNeoSettings.Enabled)
        {
            return;
        }

        var session = VSNeo_ExtensionPackage.Session;
        if (session == null || !session.IsReady)
        {
            return;
        }

        var mode = session.State.Mode;
        if (view.Selection.IsEmpty)
        {
            if (mode == VimMode.Visual)
            {
                session.Input("<Esc>");
            }

            PushCaret(view.Caret.Position.BufferPosition, force: true);
            return;
        }

        if (mode != VimMode.Normal && mode != VimMode.Visual)
        {
            return;
        }

        ConvertSelectionToVisual(view, session);
    }

    /// <summary>
    /// Draws nvim's visual selection in Visual Studio.
    ///
    /// The mode was already tracked, so v and V worked - they simply could not be
    /// seen, because only the cursor was ever synchronised and a selection needs
    /// both ends.
    ///
    /// Two conventions have to be reconciled. Vim's selection *includes* the
    /// character under the cursor; Visual Studio's ends where it ends, so the
    /// far end moves one character outward - and which end depends on the
    /// direction the selection was made in. The block marking the live end is
    /// not the real caret (Visual Studio draws no block at a selection's
    /// exclusive end) but VisualBlockCaretAdornment over nvim's cursor
    /// character. And the three flavours cover different regions from the
    /// same pair of positions: charwise runs between them, linewise takes
    /// whole lines, blockwise takes the rectangle they corner.
    /// </summary>
    private void ApplySelection(IWpfTextView view, Microsoft.VisualStudio.Text.ITextSnapshot snapshot,
                                Microsoft.VisualStudio.Text.SnapshotPoint caret,
                                VimMode mode, NvimSession? session)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        // A null session means nvim is gone; ApplyPending reports the mode as
        // Unknown then, so there cannot be a visual selection to draw - the
        // same outcome the guard below would produce, reached before touching
        // session.State.
        if (session == null)
        {
            ResetSelection(view);
            VisualBlockCaretAdornment.For(view)?.Hide();
            return;
        }

        var state = session.State;
        int anchorLine = state.VisualAnchorLine;

        if (mode != VimMode.Visual || anchorLine < 0)
        {
            ResetSelection(view);
            VisualBlockCaretAdornment.For(view)?.Hide();
            return;
        }

        var anchor = PointAt(snapshot, anchorLine, state.VisualAnchorColumn);

        // Per-line secondary selections belong only to the ragged blockwise-$
        // draw; any other visual state has to shed them first, or they linger
        // beside the new selection.
        bool ragged = state.VisualKind == '\x16' && state.VisualBlockToEol;
        if (!ragged && view is ITextView2 multiView
            && multiView.MultiSelectionBroker != null
            && multiView.MultiSelectionBroker.HasMultipleSelections)
        {
            multiView.MultiSelectionBroker.ClearSecondarySelections();
        }

        switch (state.VisualKind)
        {
            case 'V':   // linewise: whole lines, however far along each the ends are
            {
                int first = Math.Min(anchor.GetContainingLine().LineNumber,
                                     caret.GetContainingLine().LineNumber);
                int last = Math.Max(anchor.GetContainingLine().LineNumber,
                                    caret.GetContainingLine().LineNumber);

                var start = snapshot.GetLineFromLineNumber(first).Start;
                var end = last + 1 < snapshot.LineCount
                    ? snapshot.GetLineFromLineNumber(last + 1).Start
                    : new Microsoft.VisualStudio.Text.SnapshotPoint(snapshot, snapshot.Length);

                view.Selection.Mode = TextSelectionMode.Stream;
                view.Selection.Select(new Microsoft.VisualStudio.Text.SnapshotSpan(start, end), false);
                break;
            }

            case '\x16':   // blockwise: the rectangle the two corners describe
            {
                // $ in blockwise visual is not a rectangle at all: every line
                // runs to its own end. Drawn as one selection per line.
                if (state.VisualBlockToEol
                    && ApplyRaggedBlock(view, snapshot, anchor, caret))
                {
                    break;
                }

                // Vim's block includes the column the cursor is on, so whichever
                // corner is on the right has to reach one column further out.
                // Extending the caret unconditionally is wrong the moment the
                // block is drawn leftwards or upwards from its anchor: the
                // rightmost column is then the anchor's, and the rectangle came up
                // one short - missing precisely the column being pointed at.
                //
                // Compared by column within the line, not by absolute position:
                // the two corners are on different lines, so their offsets say
                // nothing about which is further right.
                int anchorColumn = anchor - anchor.GetContainingLine().Start;
                int caretColumn = caret - caret.GetContainingLine().Start;
                bool leftwards = caretColumn < anchorColumn;

                view.Selection.Mode = TextSelectionMode.Box;
                view.Selection.Select(
                    new Microsoft.VisualStudio.Text.VirtualSnapshotPoint(
                        leftwards ? Extend(snapshot, anchor) : anchor),
                    new Microsoft.VisualStudio.Text.VirtualSnapshotPoint(
                        leftwards ? caret : Extend(snapshot, caret)));
                break;
            }

            default:    // charwise
            {
                view.Selection.Mode = TextSelectionMode.Stream;

                // A SnapshotSpan is always start-then-end; direction is carried
                // by the reversed flag, not by the order of the points. Building
                // it the other way round throws, which is what o did - swapping
                // the ends of a selection puts the anchor after the cursor, and
                // the exception escaped into the dispatcher.
                bool reversed = anchor > caret;

                // Whichever end trails is the one that grows, so the character
                // under the cursor is included either way.
                var start = reversed ? caret : anchor;
                var end = reversed ? Extend(snapshot, anchor) : Extend(snapshot, caret);

                view.Selection.Select(
                    new Microsoft.VisualStudio.Text.SnapshotSpan(start, end), reversed);
                break;
            }
        }

        // The block over nvim's cursor character, marking the live end.
        // Visual Studio draws no block caret at a selection's exclusive end,
        // so without this the last character of a visual selection looked
        // unselected.
        VisualBlockCaretAdornment.For(view)?.Show(caret);
    }

    /// <summary>
    /// Draws blockwise-$: the block runs from its left edge to the end of
    /// each line, ragged, which no two-corner rectangle can express. The
    /// multi-selection broker can: one selection per line. Lines shorter
    /// than the block's left edge are not in the block, matching Vim.
    ///
    /// Returns false when the broker is unavailable, so the caller can fall
    /// back to the rectangle - approximately right beats not drawn.
    /// </summary>
    private static bool ApplyRaggedBlock(
        IWpfTextView view,
        Microsoft.VisualStudio.Text.ITextSnapshot snapshot,
        Microsoft.VisualStudio.Text.SnapshotPoint anchor,
        Microsoft.VisualStudio.Text.SnapshotPoint caret)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var broker = (view as ITextView2)?.MultiSelectionBroker;
        if (broker == null)
        {
            return false;
        }

        int anchorColumn = anchor - anchor.GetContainingLine().Start;
        int caretColumn = caret - caret.GetContainingLine().Start;
        int left = Math.Min(anchorColumn, caretColumn);

        int caretLine = caret.GetContainingLine().LineNumber;
        int first = Math.Min(anchor.GetContainingLine().LineNumber, caretLine);
        int last = Math.Max(anchor.GetContainingLine().LineNumber, caretLine);

        var selections = new List<Microsoft.VisualStudio.Text.Selection>(last - first + 1);
        Microsoft.VisualStudio.Text.Selection? primary = null;
        for (int i = first; i <= last; i++)
        {
            var line = snapshot.GetLineFromLineNumber(i);
            if (left > line.Length)
            {
                continue;
            }

            var selection = new Microsoft.VisualStudio.Text.Selection(
                new Microsoft.VisualStudio.Text.SnapshotSpan(line.Start + left, line.End), false);
            selections.Add(selection);
            if (i == caretLine)
            {
                primary = selection;
            }
        }

        // Every line shorter than the left edge, or the caret sitting on a
        // skipped line: nothing sensible to draw, and the broker's state
        // must not outlive the block either way.
        if (selections.Count == 0)
        {
            broker.ClearSecondarySelections();
            view.Selection.Clear();
            return true;
        }

        view.Selection.Mode = TextSelectionMode.Stream;
        broker.SetSelectionRange(selections, primary ?? selections[selections.Count - 1]);
        return true;
    }

    /// <summary>
    /// Back to no selection and stream mode.
    ///
    /// Clearing the selection is not enough on its own: box mode is a property of
    /// the view that outlives the selection it was set for, and a view left in it
    /// behaves like a permanently held Alt - which is how Ctrl+V made the editor
    /// unusable rather than merely mis-highlighted.
    /// </summary>
    private static void ResetSelection(IWpfTextView view)
    {
        try
        {
            if (!view.Selection.IsEmpty)
            {
                view.Selection.Clear();
            }

            if (view.Selection.Mode != TextSelectionMode.Stream)
            {
                view.Selection.Mode = TextSelectionMode.Stream;
            }

            // A ragged blockwise-$ block leaves per-line selections behind;
            // they are not part of Selection.Clear().
            if (view is ITextView2 view2
                && view2.MultiSelectionBroker != null
                && view2.MultiSelectionBroker.HasMultipleSelections)
            {
                view2.MultiSelectionBroker.ClearSecondarySelections();
            }
        }
        catch
        {
            // Never let tidying up be the thing that throws.
        }
    }

    private static Microsoft.VisualStudio.Text.SnapshotPoint PointAt(
        Microsoft.VisualStudio.Text.ITextSnapshot snapshot, int line, int byteColumn)
    {
        if (line >= snapshot.LineCount)
        {
            line = snapshot.LineCount - 1;
        }

        if (line < 0)
        {
            line = 0;
        }

        var snapshotLine = snapshot.GetLineFromLineNumber(line);
        int column = ColumnMapper.ByteToChar(snapshotLine, Math.Max(0, byteColumn));
        if (column > snapshotLine.Length)
        {
            column = snapshotLine.Length;
        }

        return snapshotLine.Start + column;
    }

    /// <summary>One character further on, without running off the line.</summary>
    private static Microsoft.VisualStudio.Text.SnapshotPoint Extend(
        Microsoft.VisualStudio.Text.ITextSnapshot snapshot,
        Microsoft.VisualStudio.Text.SnapshotPoint point)
    {
        var line = point.GetContainingLine();
        return point.Position < line.End.Position ? point + 1 : point;
    }

    private void OnCaretPositionChanged(object sender, CaretPositionChangedEventArgs e)
    {
        if (_applying)
        {
            return; // our own move, coming back around
        }

        // In visual mode the caret is not nvim's cursor. ApplySelection
        // parks it at the selection's exclusive end - one character past
        // nvim's cursor charwise, the start of the line after the last
        // selected one linewise - because that is where Visual Studio's
        // selection ends. Echoing that position back moved nvim's cursor
        // off by one, which reshaped the selection: it grew by a line per
        // V + j, j acted like k, arrows jumped, and o landed the live end
        // on the wrong character. The _applying guard only covers the
        // Select call itself; layout and scroll passes raise
        // PositionChanged afterwards too. nvim owns the cursor for the
        // whole of visual mode - every motion is a key sent to it - and
        // the one Visual Studio-side way to make a selection, the mouse,
        // hands its result over explicitly on mouse-up (with a forced
        // push, which does not come through here).
        // Operator-pending is nvim's too. Vim fixes the operator's start
        // when the operator key arrives; a cursor set while the motion is
        // still owed moves only the end, and the motion then runs from
        // the moved cursor over a range the user never typed. A caret
        // move Visual Studio made on its own in that window (a navigation
        // key a filter ahead of ours took, a click) echoed back exactly
        // so, and c<Right>k deleted two lines (issue #37).
        // Replace mode only half: every key goes to nvim (R, gR), and
        // applying its replaced line shoves the caret to the next line
        // start - echoed, that moved nvim's cursor there. So a move an edit
        // caused (the snapshot changed) stays unechoed, but one Visual Studio
        // made on its own (F3, Ctrl+], go to line) is pushed, or the next
        // character replaced at nvim's old cursor.
        var mode = VSNeo_ExtensionPackage.Session?.State.Mode ?? VimMode.Unknown;
        if (mode == VimMode.Visual || mode == VimMode.OperatorPending)
        {
            return;
        }

        if (mode == VimMode.Replace
            && e.OldPosition.BufferPosition.Snapshot != e.NewPosition.BufferPosition.Snapshot)
        {
            return;
        }
        // Insert mode the same, narrowed to nvim's own edits: the user's
        // typing also moves the caret with the snapshot, and this push is
        // what carries that caret to nvim (apply_spans does not move it).
        // A caret the drain displaced (whole lines replaced, so it slid to
        // the next line's start) is not the user's: echoed, it moved nvim's
        // cursor there and the next routed letter landed on that line.
        // ReapplyAfterEdit sets the caret from nvim's report instead.
        if (mode == VimMode.Insert
            && e.OldPosition.BufferPosition.Snapshot != e.NewPosition.BufferPosition.Snapshot
            && e.NewPosition.BufferPosition.Snapshot.TextBuffer.Properties.TryGetProperty(typeof(BufferMirror), out BufferMirror mirror)
            && e.NewPosition.BufferPosition.Snapshot.Version.VersionNumber == mirror.RemoteAppliedVersion)
        {
            return;
        }

        PushCaret(e.NewPosition.BufferPosition);
    }

    /// <summary>
    /// A mouse drag builds a selection entirely Visual Studio-side: no keys
    /// are pressed, so nothing travels over RPC, and the caret pushes the
    /// drag would cause come back as redraws that erase the selection being
    /// drawn. While the button is down the user owns the caret, exactly as
    /// in insert mode; when it comes up the finished selection is handed to
    /// nvim as a charwise visual one, so the next operator applies to it.
    /// </summary>
    private void OnMouseLeftDown(object sender, MouseButtonEventArgs e)
    {
        _mouseDown = true;
    }

    private void OnMouseLeftUp(object sender, MouseButtonEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        _mouseDown = false;

        var view = _activeView;
        if (view == null || view.IsClosed || !VSNeoSettings.Enabled)
        {
            return;
        }

        var session = VSNeo_ExtensionPackage.Session;
        if (session == null || !session.IsReady)
        {
            return;
        }

        var mode = session.State.Mode;

        if (view.Selection.IsEmpty)
        {
            // A plain click. If nvim still thinks a visual selection is
            // active - the click just erased it here - leave that mode too;
            // the caret push the suppression withheld goes now, after the
            // Escape, so it lands in normal mode.
            // A pending operator is cancelled the same way: the click used
            // to move the operator's end, and the next motion then ran over
            // a range nobody typed (c, click, k).
            if (mode == VimMode.Visual || mode == VimMode.OperatorPending)
            {
                session.Input("<Esc>");
            }

            PushCaret(view.Caret.Position.BufferPosition, force: true);
            return;
        }

        // The selection is handed over from operator-pending and replace as
        // well: nvim owns typing there, so a selection left Visual Studio-side
        // would be typed over one character at a time. vsneo.visual_select
        // leaves the mode first.
        if (mode == VimMode.OperatorPending || mode == VimMode.Replace)
        {
            ConvertSelectionToVisual(view, session);
            return;
        }

        if (mode != VimMode.Normal && mode != VimMode.Visual)
        {
            // Insert and replace: Visual Studio owns the selection and the
            // typing that replaces it, so there is nothing to hand over -
            // just keep nvim's cursor trailing the caret.
            PushCaret(view.Caret.Position.BufferPosition, force: true);
            return;
        }

        ConvertSelectionToVisual(view, session);
    }

    /// <summary>
    /// Hand a mouse-made selection to nvim as a charwise visual selection.
    ///
    /// The two conventions are reconciled again here, in the opposite
    /// direction from ApplySelection: Visual Studio's selection end is
    /// exclusive and Vim includes the character under the cursor, so the
    /// trailing end is pulled one character in - and which end trails
    /// depends on the direction the selection was dragged in.
    /// </summary>
    private void ConvertSelectionToVisual(IWpfTextView view, NvimSession session)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        // A box selection (Alt+drag) has no charwise equivalent worth
        // faking; Visual Studio keeps it.
        if (view.Selection.Mode != TextSelectionMode.Stream)
        {
            return;
        }

        var anchor = view.Selection.AnchorPoint.Position;
        var active = view.Selection.ActivePoint.Position;

        Microsoft.VisualStudio.Text.SnapshotPoint vimAnchor, vimCursor;
        if (active >= anchor)
        {
            vimAnchor = anchor;
            vimCursor = active - 1;
        }
        else
        {
            vimCursor = active;
            vimAnchor = anchor - 1;
        }

        ToLineByteColumn(vimAnchor, out int anchorLine, out int anchorColumn);
        ToLineByteColumn(vimCursor, out int cursorLine, out int cursorColumn);

        // Same convention as nvim_win_set_cursor: 1-based rows, 0-based
        // byte columns. One RPC, so the re-anchor cannot interleave with
        // anything else on the channel.
        Observe(session.RequestAsync(
            "nvim_exec_lua",
            "vsneo.visual_select(...)",
            new object[] { anchorLine + 1, anchorColumn, cursorLine + 1, cursorColumn }));

        // nvim's cursor lands on vimCursor; record it as pushed so the
        // redraw echoing this conversion is not sent straight back.
        Interlocked.Exchange(ref _lastPushed,
            ((long)cursorLine << 32) | (uint)cursorColumn);
    }

    private static void ToLineByteColumn(
        Microsoft.VisualStudio.Text.SnapshotPoint point, out int line, out int byteColumn)
    {
        var containing = point.GetContainingLine();
        line = containing.LineNumber;
        byteColumn = ColumnMapper.CharToByte(
            containing, point.Position - containing.Start.Position);
    }
}
