using System;
using System.ComponentModel.Composition;
using System.Threading;
using System.Windows.Input;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Utilities;
using VSNeo_Extension.Infrastructure;
using VSNeo_Extension.Nvim;

namespace VSNeo_Extension.Editor;

/// <summary>
/// The second interception point, and the one that gets Escape.
///
/// The KeyProcessor sees WPF key events. Visual Studio never raises one for
/// Escape: the shell translates it into VSStd2K CANCEL in the message pump and
/// routes it through the IOleCommandTarget chain, so PreviewKeyDown is simply
/// never called. The key trace shows this plainly - not one Escape event while
/// the mode cache sat in Insert forever. Ctrl+[ is the same story, and CLAUDE.md
/// already lists it.
///
/// So there are two interception points by necessity, not by preference:
/// WPF for characters and chords, the command filter for keys VS has already
/// claimed as commands. Everything not explicitly claimed here is forwarded
/// untouched.
/// </summary>
[Export(typeof(IVsTextViewCreationListener))]
[ContentType("text")]
[TextViewRole(PredefinedTextViewRoles.Editable)]
internal sealed class VsNeoCommandFilterProvider : IVsTextViewCreationListener
{
    [Import]
    internal IVsEditorAdaptersFactoryService AdapterFactory { get; set; } = null!;

    [Import]
    internal IntelliSenseGate Gate { get; set; } = null!;

    [Import]
    internal CursorSynchronizer CursorSync { get; set; } = null!;

    public void VsTextViewCreated(IVsTextView textViewAdapter)
    {
        var view = AdapterFactory.GetWpfTextView(textViewAdapter);
        if (view == null)
        {
            return;
        }

        // AddCommandFilter must happen on the UI thread; VsTextViewCreated is
        // already there. Bookkeeping only - no service resolution, no RPC.
        view.Properties.GetOrCreateSingletonProperty(
            () => new VsNeoCommandFilter(textViewAdapter, view, Gate, CursorSync));
    }
}

internal sealed partial class VsNeoCommandFilter : IOleCommandTarget
{
    private readonly IWpfTextView _view;
    private readonly IntelliSenseGate _gate;
    private readonly CursorSynchronizer _cursorSync;
    private readonly IOleCommandTarget _next;
    private readonly IOleCommandTarget _top;   // the view's whole chain, as a key enters it

    // The VSStd2K command ExecAsKey is sending through _top, or 0: this
    // filter's own Exec passes exactly that one straight on.
    private uint _passingThrough;

    // The filter of the document view holding keyboard focus, for
    // KeyPriorityTarget, which is global and has no view of its own.
    // Written from the view's focus events (UI thread), read on the UI
    // thread too; volatile only so no stale copy outlives a focus change.
    private static volatile VsNeoCommandFilter? s_focused;
    internal static VsNeoCommandFilter? Focused => s_focused;

    // TickCount when KeyPriorityTarget handled this view's Escape; 0 when
    // none is outstanding. The same keystroke then reaches Exec here - unless
    // it was swallowed, or a filter ahead of this one took it - and must not
    // be sent to nvim a second time. Expires, so a claim whose keystroke
    // never arrived cannot swallow a later, unrelated Escape.
    private int _escapeClaimedAt;
    private const int EscapeClaimWindowMs = 500;

    static VsNeoCommandFilter()
    {
        VerifyCommandKeyTable();
    }

    /// <summary>
    /// <see cref="CommandKeyTable"/> duplicates the SDK's command ids so the
    /// table can be tested without devenv. Release builds compile the call
    /// out; a Debug build trips here on the first view if a constant drifts.
    /// </summary>
    [System.Diagnostics.Conditional("DEBUG")]
    private static void VerifyCommandKeyTable()
    {
        System.Diagnostics.Debug.Assert(CommandKeyTable.VsStd2K == VSConstants.VSStd2K);
        System.Diagnostics.Debug.Assert(
            CommandKeyTable.VsStd97 == VSConstants.GUID_VSStandardCommandSet97);
        System.Diagnostics.Debug.Assert(CommandKeyTable.Backspace == (uint)VSConstants.VSStd2KCmdID.BACKSPACE);
        System.Diagnostics.Debug.Assert(CommandKeyTable.Return == (uint)VSConstants.VSStd2KCmdID.RETURN);
        System.Diagnostics.Debug.Assert(CommandKeyTable.Tab == (uint)VSConstants.VSStd2KCmdID.TAB);
        System.Diagnostics.Debug.Assert(CommandKeyTable.BackTab == (uint)VSConstants.VSStd2KCmdID.BACKTAB);
        System.Diagnostics.Debug.Assert(CommandKeyTable.Delete == (uint)VSConstants.VSStd2KCmdID.DELETE);
        System.Diagnostics.Debug.Assert(CommandKeyTable.Left == (uint)VSConstants.VSStd2KCmdID.LEFT);
        System.Diagnostics.Debug.Assert(CommandKeyTable.LeftExt == (uint)VSConstants.VSStd2KCmdID.LEFT_EXT);
        System.Diagnostics.Debug.Assert(CommandKeyTable.Right == (uint)VSConstants.VSStd2KCmdID.RIGHT);
        System.Diagnostics.Debug.Assert(CommandKeyTable.RightExt == (uint)VSConstants.VSStd2KCmdID.RIGHT_EXT);
        System.Diagnostics.Debug.Assert(CommandKeyTable.Up == (uint)VSConstants.VSStd2KCmdID.UP);
        System.Diagnostics.Debug.Assert(CommandKeyTable.UpExt == (uint)VSConstants.VSStd2KCmdID.UP_EXT);
        System.Diagnostics.Debug.Assert(CommandKeyTable.Down == (uint)VSConstants.VSStd2KCmdID.DOWN);
        System.Diagnostics.Debug.Assert(CommandKeyTable.DownExt == (uint)VSConstants.VSStd2KCmdID.DOWN_EXT);
        System.Diagnostics.Debug.Assert(CommandKeyTable.Home == (uint)VSConstants.VSStd2KCmdID.HOME);
        System.Diagnostics.Debug.Assert(CommandKeyTable.HomeExt == (uint)VSConstants.VSStd2KCmdID.HOME_EXT);
        System.Diagnostics.Debug.Assert(CommandKeyTable.End == (uint)VSConstants.VSStd2KCmdID.END);
        System.Diagnostics.Debug.Assert(CommandKeyTable.EndExt == (uint)VSConstants.VSStd2KCmdID.END_EXT);
        System.Diagnostics.Debug.Assert(CommandKeyTable.Bol == (uint)VSConstants.VSStd2KCmdID.BOL);
        System.Diagnostics.Debug.Assert(CommandKeyTable.BolExt == (uint)VSConstants.VSStd2KCmdID.BOL_EXT);
        System.Diagnostics.Debug.Assert(CommandKeyTable.FirstChar == (uint)VSConstants.VSStd2KCmdID.FIRSTCHAR);
        System.Diagnostics.Debug.Assert(CommandKeyTable.FirstCharExt == (uint)VSConstants.VSStd2KCmdID.FIRSTCHAR_EXT);
        System.Diagnostics.Debug.Assert(CommandKeyTable.Eol == (uint)VSConstants.VSStd2KCmdID.EOL);
        System.Diagnostics.Debug.Assert(CommandKeyTable.EolExt == (uint)VSConstants.VSStd2KCmdID.EOL_EXT);
        System.Diagnostics.Debug.Assert(CommandKeyTable.PageUp == (uint)VSConstants.VSStd2KCmdID.PAGEUP);
        System.Diagnostics.Debug.Assert(CommandKeyTable.PageUpExt == (uint)VSConstants.VSStd2KCmdID.PAGEUP_EXT);
        System.Diagnostics.Debug.Assert(CommandKeyTable.PageDown == (uint)VSConstants.VSStd2KCmdID.PAGEDN);
        System.Diagnostics.Debug.Assert(CommandKeyTable.PageDownExt == (uint)VSConstants.VSStd2KCmdID.PAGEDN_EXT);
        System.Diagnostics.Debug.Assert(CommandKeyTable.WordPrev == (uint)VSConstants.VSStd2KCmdID.WORDPREV);
        System.Diagnostics.Debug.Assert(CommandKeyTable.WordPrevExt == (uint)VSConstants.VSStd2KCmdID.WORDPREV_EXT);
        System.Diagnostics.Debug.Assert(CommandKeyTable.WordNext == (uint)VSConstants.VSStd2KCmdID.WORDNEXT);
        System.Diagnostics.Debug.Assert(CommandKeyTable.WordNextExt == (uint)VSConstants.VSStd2KCmdID.WORDNEXT_EXT);
        System.Diagnostics.Debug.Assert(CommandKeyTable.Cancel == (uint)VSConstants.VSStd2KCmdID.CANCEL);
        System.Diagnostics.Debug.Assert(CommandKeyTable.Paste == (uint)VSConstants.VSStd97CmdID.Paste);
    }

    public VsNeoCommandFilter(
        IVsTextView adapter, IWpfTextView view, IntelliSenseGate gate, CursorSynchronizer cursorSync)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _view = view;
        _gate = gate;
        _cursorSync = cursorSync;
        _top = (IOleCommandTarget)adapter;
        adapter.AddCommandFilter(this, out _next);

        if (view.HasAggregateFocus)
        {
            s_focused = this;
        }

        view.GotAggregateFocus += OnGotFocus;
        view.LostAggregateFocus += OnLostFocus;
        view.Closed += OnClosed;
    }

    private void OnGotFocus(object sender, EventArgs e) => s_focused = this;

    private void OnLostFocus(object sender, EventArgs e)
    {
        if (ReferenceEquals(s_focused, this))
        {
            s_focused = null;
        }
    }

    private void OnClosed(object sender, EventArgs e)
    {
        OnLostFocus(sender, e);
        _view.GotAggregateFocus -= OnGotFocus;
        _view.LostAggregateFocus -= OnLostFocus;
        _view.Closed -= OnClosed;
    }

    public int Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
    {
        // Visual Studio only ever routes commands to a view's filter chain on
        // the UI thread; stating it is what lets the analyzer prove every
        // main-thread-only access below.
        ThreadHelper.ThrowIfNotOnUIThread();

        // With VSNEO_TRACE_KEYS=1 this names every command Visual Studio routes
        // through the view. It is how to find out what a chord actually becomes -
        // Ctrl+D and Ctrl+U turn into commands that can be claimed here, while a
        // chord *prefix* like Ctrl+E fires nothing at all and simply waits for a
        // second key, which is a different problem needing a different fix.
        Infrastructure.Log.Key("Exec " + Describe(pguidCmdGroup, nCmdID));

        if (_passingThrough != 0 && nCmdID == _passingThrough && pguidCmdGroup == VSConstants.VSStd2K)
        {
            return Forward(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut);
        }

        // Same eligibility rule as the key processor: only document views are
        // ours. The C# Interactive window is an editable text view too, and it
        // owns its keystrokes outright - in Normal mode we would otherwise
        // steal its paste (Ctrl+V) and every command-line key.
        if (!_view.Roles.Contains(PredefinedTextViewRoles.Document) || !VSNeoSettings.Enabled)
        {
            return Forward(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut);
        }

        // Long command output on screen (MessagePager) takes the keys Visual
        // Studio makes commands of - Escape and Enter close it, the arrows
        // and paging keys scroll it - ahead of every other claim here.
        if (MessagePager.OpenFor(_view) is MessagePager pager)
        {
            var pagerKey = PagerKeyFor(pguidCmdGroup, nCmdID);
            if (pagerKey != null && pager.HandleKey(pagerKey))
            {
                return VSConstants.S_OK;
            }
        }

        var focusedElement = Keyboard.FocusedElement;
        if (focusedElement != null && !ReferenceEquals(focusedElement, _view.VisualElement))
        {
            return Forward(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut);
        }

        var routingSession = VSNeo_ExtensionPackage.Session;
        if (routingSession != null && routingSession.IsReady)
        {
            string? routedKeys = IsCancel(pguidCmdGroup, nCmdID) ? "<Esc>"
                : IsPaste(pguidCmdGroup, nCmdID) ? "<C-v>"
                : CmdLineKeyFor(pguidCmdGroup, nCmdID) ?? NormalModeKeyFor(pguidCmdGroup, nCmdID);
            var routingMode = routingSession.State.Mode;
            var owner = routingSession.State.Routing.OwnerFor(routedKeys, routingMode);
            if (owner == KeyOwner.Ide)
            {
                return Forward(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut);
            }

            if (owner == KeyOwner.Vim && routedKeys != null
                && routingMode != VimMode.Replace && routingMode != VimMode.CmdLine
                && routedKeys != "<Esc>")
            {
                // Not the command line: TryHandleCmdLine already sends its
                // keys to nvim, and sent from here they skipped its Ctrl+V
                // paste (a literal-next instead, then ^M on Enter) and its
                // guard against <BS> on an empty line, which drops out of
                // the command line and repeats into the buffer.
                if (routingMode == VimMode.Insert)
                {
                    _cursorSync.SyncCaretToNvim(force: true);
                    _cursorSync.AllowNextInsertApply();
                }

                if (routingMode == VimMode.Normal || routingMode == VimMode.Visual)
                {
                    BufferMirror.TryGetForBuffer(_view.TextBuffer)?.ForgetPendingInsertUndoStart();
                }

                routingSession.Input(routedKeys);
                Infrastructure.Log.Key("sethandler vim -> sent " + routedKeys);
                return VSConstants.S_OK;
            }
        }

        // An overlay interaction (jump labels, anything Lua drives) owns
        // the keys Visual Studio turns into commands before WPF can see
        // them, exactly as the command line does. Printable characters
        // already reach nvim through the key processor in Normal mode.
        var overlaySession = VSNeo_ExtensionPackage.Session;
        if (overlaySession != null && overlaySession.IsReady
            && overlaySession.State.OverlayActive)
        {
            if (IsCancel(pguidCmdGroup, nCmdID))
            {
                overlaySession.Input("<Esc>");
                return VSConstants.S_OK;
            }

            var overlayKeys = CmdLineKeyFor(pguidCmdGroup, nCmdID);
            if (overlayKeys != null)
            {
                overlaySession.Input(overlayKeys);
                return VSConstants.S_OK;
            }
        }

        if (TryHandleInsertMap(pguidCmdGroup, nCmdID))
        {
            return VSConstants.S_OK;
        }

        if (TryHandleReplaceKey(pguidCmdGroup, nCmdID))
        {
            return VSConstants.S_OK;
        }

        if (TryRouteBehindRemoteEdits(pguidCmdGroup, nCmdID))
        {
            return VSConstants.S_OK;
        }

        // Already sent to nvim by KeyPriorityTarget: the key is only
        // passing through now, on its way to a completion list.
        if (IsCancel(pguidCmdGroup, nCmdID) && ConsumeEscapeClaim())
        {
            return Forward(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut);
        }

        if (IsCancel(pguidCmdGroup, nCmdID) && TryHandleEscape(out bool swallow) && swallow)
        {
            return VSConstants.S_OK;
        }

        if (IsPaste(pguidCmdGroup, nCmdID) && TryHandleBlockwise())
        {
            return VSConstants.S_OK;
        }

        if (TryHandleCmdLine(pguidCmdGroup, nCmdID))
        {
            return VSConstants.S_OK;
        }

        if (TryHandleNormalModeKey(pguidCmdGroup, nCmdID))
        {
            return VSConstants.S_OK;
        }

        return Forward(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut);
    }

    private static string Describe(Guid group, uint id)
    {
        if (group == VSConstants.VSStd2K)
        {
            return "VSStd2K." + SafeName(typeof(VSConstants.VSStd2KCmdID), id);
        }

        if (group == VSConstants.GUID_VSStandardCommandSet97)
        {
            return "VSStd97." + SafeName(typeof(VSConstants.VSStd97CmdID), id);
        }

        return group.ToString("D") + ":" + id;
    }

    private static string SafeName(Type enumType, uint id)
    {
        try
        {
            var name = Enum.GetName(enumType, (int)id);
            return name ?? id.ToString();
        }
        catch
        {
            return id.ToString();
        }
    }

    private static bool IsCancel(Guid group, uint id) => CommandKeyTable.IsCancel(group, id);

    private static bool IsPaste(Guid group, uint id) => CommandKeyTable.IsPaste(group, id);

    private int Forward(ref Guid group, uint id, uint opt, IntPtr pvaIn, IntPtr pvaOut)
    {
        // Only reached from Exec, but the analyzer cannot see through the call,
        // so the UI-thread contract has to be restated here.
        ThreadHelper.ThrowIfNotOnUIThread();
        return _next == null
            ? (int)Constants.OLECMDERR_E_NOTSUPPORTED
            : _next.Exec(ref group, id, opt, pvaIn, pvaOut);
    }

    /// <summary>
    /// Runs an editor command through the view's whole filter chain, the
    /// way the key it stands for arrives: i_CTRL-N and i_CTRL-P
    /// (VsNeoKeyProcessor) walk an open completion list as Down/Up. From the
    /// top, because a completion UI whose filter sits ahead of this one
    /// (ReSharper's lookup, legacy language services) owns the list's keys;
    /// sent from below it, Down moved the caret instead. This filter steps
    /// aside for that one command, or a Down the user mapped in insert
    /// (imap &lt;Down&gt;) would go to nvim.
    /// </summary>
    internal void ExecAsKey(VSConstants.VSStd2KCmdID id)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var group = VSConstants.VSStd2K;
        _passingThrough = (uint)id;
        try
        { _top.Exec(ref group, (uint)id, 0, IntPtr.Zero, IntPtr.Zero); }
        finally { _passingThrough = 0; }
    }

    public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return _next == null
            ? (int)Constants.OLECMDERR_E_NOTSUPPORTED
            : _next.QueryStatus(ref pguidCmdGroup, cCmds, prgCmds, pCmdText);
    }
}
