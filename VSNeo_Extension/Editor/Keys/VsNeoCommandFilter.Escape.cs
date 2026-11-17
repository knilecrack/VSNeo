using System;
using System.Threading;
using System.Windows.Input;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;
using VSNeo_Extension.Infrastructure;
using VSNeo_Extension.Nvim;

namespace VSNeo_Extension.Editor;

// Escape and navigation claims, ahead of the view chain.
internal sealed partial class VsNeoCommandFilter
{
    /// <summary>
    /// The arrows, Home/End, PageUp/PageDown, Delete and Backspace in
    /// normal, visual and operator-pending mode, claimed from the shell's
    /// priority target - ahead of every filter in the view's chain, for
    /// the reason Escape is: a filter added after ours runs first, and one
    /// that handles RIGHT without forwarding it starves this filter of
    /// the key. c&lt;Right&gt; then left the c pending and the next letter
    /// completed it (ch, cl, ck, cc). The routes that outrank the
    /// normal-mode one in Exec keep their keys: the pager's, and the
    /// command line's history and wildmenu arrows.
    /// </summary>
    internal bool TryClaimNavigationKey(Guid group, uint id)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (!_view.Roles.Contains(PredefinedTextViewRoles.Document) || _view.IsClosed
            || !VSNeoSettings.Enabled)
        {
            return false;
        }

        if (MessagePager.OpenFor(_view) != null)
        {
            return false;
        }

        var session = VSNeo_ExtensionPackage.Session;
        if (session == null || !session.IsReady || session.State.OverlayActive)
        {
            return false;
        }

        var routedKeys = CmdLineKeyFor(group, id) ?? NormalModeKeyFor(group, id);
        if (session.State.Routing.OwnerFor(routedKeys, session.State.Mode) == KeyOwner.Ide)
        {
            return false;
        }

        // Replace mode's keys are nvim's too (R, gR), and a filter ahead of
        // ours taking <Right> would leave the next character replacing at a
        // stale cursor.
        if (session.State.Mode == VimMode.Replace)
        {
            return TryHandleReplaceKey(group, id);
        }

        if (NormalModeKeyFor(group, id) == null)
        {
            return false;
        }

        // Mode and focus are checked inside; insert and command-line mode
        // decline, and the key takes the ordinary chain.
        if (!TryHandleNormalModeKey(group, id))
        {
            return false;
        }

        Log.Key("  (navigation key claimed by the priority target)");
        return true;
    }

    /// <summary>
    /// Escape as KeyPriorityTarget sees it, ahead of every command filter
    /// on the view. Claimed only in insert/replace, only on a document view
    /// whose editor surface really has focus, and never while an overlay
    /// owns the keys; everything else keeps its ordinary route through
    /// Exec. Returns true when handled; <paramref name="swallow"/> is the
    /// same decision Exec makes (a completion list still gets the key).
    /// </summary>
    internal bool TryClaimInsertEscape(out bool swallow)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        swallow = false;

        if (!_view.Roles.Contains(PredefinedTextViewRoles.Document) || _view.IsClosed
            || !VSNeoSettings.Enabled)
        {
            return false;
        }

        var focused = Keyboard.FocusedElement;
        if (focused == null || !ReferenceEquals(focused, _view.VisualElement))
        {
            return false;
        }

        // The pager owns the keys while it is open (Escape closes it), and
        // Exec handles that - but this runs before Exec, and an insert-mode
        // pager is real: <C-o>:messages<CR> opens one and returns to insert.
        if (MessagePager.OpenFor(_view) != null)
        {
            return false;
        }

        var session = VSNeo_ExtensionPackage.Session;
        if (session == null || !session.IsReady || session.State.OverlayActive)
        {
            return false;
        }

        var mode = session.State.Mode;
        if (mode != VimMode.Insert && mode != VimMode.Replace)
        {
            return false;
        }

        if (session.State.Routing.OwnerFor("<Esc>", mode) == KeyOwner.Ide)
        {
            return false;
        }

        if (!TryHandleEscape(out swallow))
        {
            return false;
        }

        // The claim is only for a keystroke that will still pass through Exec
        // on its way to a completion list. A swallowed Escape never gets
        // there, so a claim for it was consumed by the *next* Escape instead -
        // forwarded to Visual Studio without reaching nvim, which left an
        // operator or count pending after <Esc>d<Esc> typed quickly.
        if (!swallow)
        {
            int stamp = Environment.TickCount | 1;
            Volatile.Write(ref _escapeClaimedAt, stamp);

            // Exec runs inside this same dispatch, so by the time a
            // Decoration item runs the keystroke is over. A claim still
            // standing then belongs to an Escape that a filter ahead of ours
            // took (the completion list closing): left alone it was consumed
            // by the next Escape, <Esc>d<Esc> typed quickly, which went to
            // Visual Studio instead of nvim and left `d` pending.
#pragma warning disable VSTHRD001
            _ = _view.VisualElement.Dispatcher.BeginInvoke(
                UiPriority.Decoration,
                new Action(() => Interlocked.CompareExchange(ref _escapeClaimedAt, 0, stamp)));
#pragma warning restore VSTHRD001
        }

        Log.Key("  (Escape claimed by the priority target, swallow=" + swallow + ")");
        return true;
    }

    /// <summary>True, once, when this keystroke's Escape was already handled up front.</summary>
    private bool ConsumeEscapeClaim()
    {
        int at = Interlocked.Exchange(ref _escapeClaimedAt, 0);
        return at != 0 && unchecked(Environment.TickCount - at) < EscapeClaimWindowMs;
    }

    /// <summary>
    /// Returns false when Escape is none of our business. When it is ours,
    /// <paramref name="swallow"/> decides whether Visual Studio still gets it.
    /// </summary>
    private bool TryHandleEscape(out bool swallow)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        swallow = false;

        var session = VSNeo_ExtensionPackage.Session;
        if (session == null || !session.IsReady)
        {
            return false;
        }

        var mode = session.State.Mode;

        // A completion list has to be closable with Escape - but closing it and
        // consuming the key are different things. Handing Escape to IntelliSense
        // and stopping there is why leaving insert mode sometimes took two
        // presses: C# completion opens constantly while typing, so the first
        // Escape went to the list and only the second reached nvim. One Escape
        // should always land you in normal mode. nvim is told either way; VS is
        // additionally allowed to see the key when there is a list to dismiss,
        // so both things happen on the one press.
        bool listOpen = _gate.IsActive(_view);

        // Opted out of the one-press rule (vim.g.vsneo_esc_closes_popup):
        // this Escape belongs to the popup alone and insert mode stays.
        // Not ours, so Exec forwards it to Visual Studio untouched and the
        // priority target does not claim it; the next Escape, with the
        // popup gone, leaves insert as usual.
        if (listOpen && session.State.EscClosesPopup
            && session.State.Routing.OwnerFor("<Esc>", mode) != KeyOwner.Vim
            && (mode == VimMode.Insert || mode == VimMode.Replace))
        {
            Log.Key("CANCEL -> popup only (vsneo_esc_closes_popup), staying in " + mode);
            return false;
        }

        // Tell nvim where the caret actually is before asking it to leave insert.
        // Visual Studio handled every keystroke of that insert session on its
        // own, so nvim's cursor is only as current as the last push it managed to
        // apply - and Escape moves the cursor one column left relative to
        // wherever nvim thinks it is. Correcting the position first is what makes
        // that land on the character you were actually typing next to.
        if (mode == VimMode.Insert || mode == VimMode.Replace)
        {
            _cursorSync?.SyncCaretToNvim(force: true);
        }

        session.Input("<Esc>");
        if (mode == VimMode.Replace)
        {
            VsNeoKeyProcessor.ArmReplaceEscape(session);
        }

        Log.Key(
            "CANCEL -> sent <Esc> to nvim, mode was " + mode + ", completion open=" + listOpen);

        // Swallowed only when nobody else needs it. Normal mode keeps Visual
        // Studio's own Escape behaviours - peek windows, light bulbs - because
        // there Escape is a Vim no-op that merely clears a pending count.
        swallow = !listOpen && mode != VimMode.Normal;
        return true;
    }
}
