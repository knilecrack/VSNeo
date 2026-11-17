using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using VSNeo_Extension.Infrastructure;
using VSNeo_Extension.Nvim;

namespace VSNeo_Extension.Editor;

// Named keys and chords, and the Ctrl-O / replace-Escape windows they open.
internal sealed partial class VsNeoKeyProcessor
{
    /// <summary>
    /// Named keys and modified chords: Esc, CR, arrows, &lt;C-w&gt;. Anything that
    /// has no character to speak of, or whose character would be wrong.
    /// </summary>
    public override void PreviewKeyDown(KeyEventArgs args)
    {
        using var perf = Infrastructure.Perf.Time("VsNeoKeyProcessor.PreviewKeyDown");
        // WPF key events arrive on the UI thread; SyncCaretToNvim requires
        // it, and stating it lets the analyzer prove the call below.
        ThreadHelper.ThrowIfNotOnUIThread();
        if (!VSNeoSettings.Enabled)
        {
            EnsureOverwriteOff();
            return;
        }

        // Snapshot once: the session can be swapped out by a reconnect, and a
        // half-handled key is worse than one we never claimed.
        var session = Session;

        Infrastructure.Log.Key("PreviewKeyDown " + args.Key
            + (args.Key == Key.System ? "/" + args.SystemKey : "")
            + " mods=" + Keyboard.Modifiers
            + " mode=" + (session == null ? "<no session>" : session.State.Mode.ToString())
            + " ready=" + (session?.IsReady == true)
            + " focus=" + _view.HasAggregateFocus);

        // Long command output is on screen (MessagePager): it owns the keys
        // the way Vim's more prompt does. Letters arrive through TextInput;
        // named keys and chords come through here.
        if (IsDocumentView && _view.HasAggregateFocus && !ForeignFocus()
            && MessagePager.OpenFor(_view) is MessagePager pager)
        {
            var pagerKey = KeyEncoder.Encode(args);
            if (pagerKey != null && pager.HandleKey(pagerKey))
            {
                args.Handled = true;
                return;
            }
        }

        // Ctrl+W in insert is claimed even while a completion list is open:
        // the list has no use for the chord and Visual Studio's own binding
        // is long gone (KeyBindingCleaner), so passing it through would make
        // the key do nothing at all - which is exactly what happened while
        // typing fresh text, where C# completion is almost always up.
        // Not while a <C-o> excursion is pending: there Ctrl+W is nvim's
        // window-command prefix, not delete-word-backward.
        // Resolved before the check: a <C-o> excursion that already completed
        // leaves the flag set until the next key looks at it, and that key
        // used to be this Ctrl+W - skipped here as "excursion pending", then
        // ignored by the insert branch too, so the first Ctrl+W after <C-o>zz
        // did nothing at all.
        VimMode? resolved = session != null ? ResolveCtrlO(session, session.State.Mode) : (VimMode?)null;
        var encoded = KeyEncoder.Encode(args);
        var owner = session != null && resolved.HasValue
            ? session.State.Routing.OwnerFor(encoded, resolved.Value) : KeyOwner.Unspecified;
        if (owner == KeyOwner.Ide)
        {
            return;
        }

        bool forceVim = owner == KeyOwner.Vim;

        if (session != null
            && InsertKeyDecision.ShouldDeleteWordBackward(
                session.IsReady, IsDocumentView, _view.HasAggregateFocus, ForeignFocus(),
                _ctrlOPending, forceVim, resolved ?? VimMode.Unknown,
                args.Key == Key.W && Keyboard.Modifiers == ModifierKeys.Control))
        {
            Infrastructure.Log.Key("  -> <C-w> delete word backward (VS-side)");
            DeleteWordBackward(session);
            args.Handled = true;
            return;
        }

        // i_CTRL-N / i_CTRL-P, Visual Studio-side like the rest of insert:
        // next / previous in an open completion list, else open one (Vim
        // completes there too). Ahead of ShouldIntercept for Ctrl+W's
        // reason - an open list is exactly when they matter. KeyBindingCleaner
        // takes the chords from File.NewFile and File.Print, or they would
        // never reach WPF. A user's imap of either key wins - except while a
        // popup is open, where ShouldIntercept never runs an imap and the
        // freed chord would do nothing at all. :Sethandler wins always
        // (i:vim sends the key to nvim, i:ide leaves it to VS).
        if (session != null
            && InsertKeyDecision.ShouldRouteCompletion(
                session.IsReady, IsDocumentView, _view.HasAggregateFocus, ForeignFocus(),
                _ctrlOPending, forceVim, resolved ?? VimMode.Unknown,
                (args.Key == Key.N || args.Key == Key.P) && Keyboard.Modifiers == ModifierKeys.Control,
                encoded != null && session.State.IsInsertMapped(encoded),
                _gate.IsActive(_view))
            && _view.Properties.TryGetProperty(typeof(VsNeoCommandFilter), out VsNeoCommandFilter filter))
        {
            var id = !_gate.IsCompletionActive(_view) ? Microsoft.VisualStudio.VSConstants.VSStd2KCmdID.COMPLETEWORD
                   : args.Key == Key.N ? Microsoft.VisualStudio.VSConstants.VSStd2KCmdID.DOWN
                   : Microsoft.VisualStudio.VSConstants.VSStd2KCmdID.UP;
            Infrastructure.Log.Key("  -> <C-" + (args.Key == Key.N ? "n" : "p") + "> completion " + id);
            filter.ExecAsKey(id);
            args.Handled = true;
            return;
        }

        if (!ShouldIntercept(session!, forceVim))
        {
            return;
        }

        if (session is null)
        {
            Infrastructure.Log.Key("Session is null");
            return;
        }

        VimMode mode = resolved!.Value;

        // Insert mode passes through so IntelliSense, snippets, and brace
        // completion keep working. Escape, Ctrl+O, user mappings and keys
        // explicitly assigned to Vim by :Sethandler are still claimed.
        if (mode is VimMode.Insert or VimMode.Replace)
        {
            EnsureOverwriteOff();
            if (InsertKeyDecision.ShouldSendEscape(mode, args.Key == Key.Escape))
            {
                // Normally the command filter sees Escape (VS turns it into
                // a command before WPF does) and syncs the caret there; if
                // the key ever reaches this branch it needs the same.
                Infrastructure.Log.Key("  -> sending <Esc> to leave insert");
                _cursorSync.SyncCaretToNvim(force: true);
                session.Input("<Esc>");
                if (mode == VimMode.Replace)
                {
                    ArmReplaceEscape(session);
                }

                args.Handled = true;
            }
            else if (InsertKeyDecision.ShouldSendReplaceKey(mode, encoded != null))
            {
                // Replace mode is nvim's (R, gR): chords too, <C-w> and
                // <C-o> included - nvim runs them natively there.
                if (encoded != null)
                {
                    session.Input(encoded);
                    // <C-o> arms like insert's: until the niR push lands,
                    // a quick `u` would otherwise go to nvim as replace text
                    // and run nvim's undo. No caret sync - nvim owns it here.
                    if (encoded == "<C-o>")
                    {
                        _ctrlOPending = true;
                        _ctrlOArmSequence = session.State.ModeSequence;
                        _ctrlOArmTicks = Environment.TickCount;
                    }

                    args.Handled = true;
                }
            }
            else if (InsertKeyDecision.ShouldClaimCtrlO(
                mode, args.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control))
            {
                // i_CTRL-O: one normal-mode command, then back to insert.
                // File.OpenFile is already unbound (KeyBindingCleaner), so
                // the chord reaches WPF. The arming covers the gap before
                // the niI push lands - see ResolveCtrlO.
                //
                // Sync the caret first, for the same reason Escape does
                // (VsNeoCommandFilter): Visual Studio owned the caret for
                // the whole insert session, so nvim's cursor is a lagging
                // echo - and the command about to run is relative to it.
                Infrastructure.Log.Key("  -> <C-o> one normal-mode command");
                _cursorSync.SyncCaretToNvim(force: true);
                session.Input("<C-o>");
                _ctrlOPending = true;
                _ctrlOArmSequence = session.State.ModeSequence;
                _ctrlOArmTicks = Environment.TickCount;
                args.Handled = true;
            }
            else
            {
                // A sethandler vim rule opts in directly; otherwise only
                // the user's own named-key insert mappings are claimed.
                if (encoded != null
                    && InsertKeyDecision.ShouldClaimInsertMap(
                        mode, true, forceVim, session.State.IsInsertMapped(encoded)))
                {
                    Infrastructure.Log.Key("  -> insert map/handler " + encoded);
                    _cursorSync.SyncCaretToNvim(force: true);
                    _cursorSync.AllowNextInsertApply();
                    session.Input(encoded);
                    args.Handled = true;
                }
            }

            return;
        }

        // Ctrl+R is redo, performed Visual Studio-side like u (see TextInput):
        // nvim's redo tree mirrors its undo tree, and neither is authoritative.
        if (InsertKeyDecision.ShouldRedo(
            mode, args.Key == Key.R && Keyboard.Modifiers == ModifierKeys.Control))
        {
            Infrastructure.Log.Key("  -> redo (VS-side)");
            ResetWhichKey();
            DropPendingCount(session);
            UndoRedo(undo: false);
            ReleaseCtrlO(session);
            args.Handled = true;
            return;
        }

        var keys = encoded;
        if (keys == null)
        {
            return;
        }

        ForgetPendingInsertUndoStart(mode);
        session.Input(keys);
        args.Handled = true;
        TrackWhichKey(session, keys, mode);
    }

    /// <summary>
    /// Effective routing mode while an i_CTRL-O excursion is live. nvim
    /// reports the excursion as niI, which the hub maps to Normal, so once
    /// the push lands ordinary routing takes over - but the very next key
    /// IS the command, and a fast typist beats the push by milliseconds.
    /// While armed with the cache still saying insert, route as normal
    /// anyway, or the command passes through as typed text.
    ///
    /// Disarming counts transitions, not values: the i -> niI -> i round
    /// trip is two effective-mode changes, and both pushes can land while
    /// the UI thread is busy, so by the next key the mode already reads
    /// "Insert" again - indistinguishable from "the excursion never
    /// happened" without the sequence. The tick guard drops the flag when
    /// the niI push never comes at all (an inoremap on &lt;C-o&gt;, an error
    /// swallowing the chord): without it the whole insert session would
    /// route to nvim as normal-mode keys forever.
    /// </summary>
    private VimMode ResolveCtrlO(NvimSession session, VimMode mode)
    {
        CtrlODecision.Result replace = CtrlODecision.ResolveReplaceEscape(
            mode, session.State.ModeSequence, Environment.TickCount,
            s_replaceEscPending, s_replaceEscSequence, s_replaceEscTicks);
        s_replaceEscPending = replace.Pending;
        if (replace.Resolved)
        {
            return replace.Mode;
        }

        CtrlODecision.Result ctrlO = CtrlODecision.ResolveCtrlO(
            mode, session.State.ModeSequence, Environment.TickCount,
            _ctrlOPending, _ctrlOArmSequence, _ctrlOArmTicks);
        _ctrlOPending = ctrlO.Pending;
        return ctrlO.Mode;
    }

    // <Esc> sent from replace mode, UI thread only. Every key in replace
    // goes to nvim as text, so until the R -> n push lands a quick u after
    // <Esc> reached nvim and ran its own undo. Armed, the cache's Replace
    // resolves as Normal (u and <C-r> stay VS-side) until the mode sequence
    // moves. Static: Escape arrives through the command filter, and only
    // the focused view types.
    private static bool s_replaceEscPending;
    private static int s_replaceEscSequence;
    private static int s_replaceEscTicks;

    internal static void ArmReplaceEscape(NvimSession session)
    {
        s_replaceEscPending = true;
        s_replaceEscSequence = session.State.ModeSequence;
        s_replaceEscTicks = Environment.TickCount;
    }

    /// <summary>
    /// A VS-side command consumed the one normal command nvim is waiting
    /// for (u, Ctrl+R): nothing will ever arrive, so release nvim from niI
    /// back into insert. Escape in niI cancels the pending command without
    /// leaving insert - exactly the semantics i_CTRL-O promises.
    /// </summary>
    private void ReleaseCtrlO(NvimSession session)
    {
        if (!_ctrlOPending)
        {
            return;
        }

        session.Input("<Esc>");
    }

    /// <summary>
    /// A count or register prefix typed before u / &lt;C-r&gt; went to nvim
    /// as keys, the undo key did not: after 3u nvim still held the 3 and
    /// the next j moved three lines. &lt;Esc&gt; drops it. Reads the cached
    /// showcmd only. Not during a &lt;C-o&gt; excursion: there ReleaseCtrlO's
    /// own &lt;Esc&gt; drops it, and a second one would leave insert.
    /// </summary>
    private void DropPendingCount(NvimSession session)
    {
        if (_ctrlOPending || string.IsNullOrEmpty(session.State.ShowCmd))
        {
            return;
        }

        Infrastructure.Log.Key("  -> <Esc> drops pending '" + session.State.ShowCmd + "'");
        session.Input("<Esc>");
    }

    /// <summary>
    /// A key going to nvim in normal or visual mode ends the window in which
    /// the mirror's pending insert-undo start is valid (zero I/O: a property
    /// lookup). Insert and replace keys never clear it.
    /// </summary>
    private void ForgetPendingInsertUndoStart(VimMode mode)
    {
        if (mode is VimMode.Normal or VimMode.Visual)
        {
            BufferMirror.TryGetForBuffer(_view.TextBuffer)?.ForgetPendingInsertUndoStart();
        }
    }
}
