using System.ComponentModel.Composition;
using System.Windows.Input;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using VSNeo_Extension.Nvim;

namespace VSNeo_Extension.Editor;

/// <summary>
/// The earliest sanctioned interception point in the editor: WPF PreviewKeyDown,
/// ahead of Visual Studio's command routing.
///
/// Attachment is cheap and eager. Activation is async and lives in NvimSession.
/// Nothing in this file may perform I/O or block.
/// </summary>
[Export(typeof(IKeyProcessorProvider))]
[Name("VSNeoKeyProcessor")]
[Order(Before = "VisualStudioKeyProcessor")]
[ContentType("text")]
[TextViewRole(PredefinedTextViewRoles.Editable)]
internal sealed class VsNeoKeyProcessorProvider : IKeyProcessorProvider
{
    [Import]
    internal IntelliSenseGate Gate { get; set; } = null!;

    [Import]
    internal CursorSynchronizer CursorSync { get; set; } = null!;

    [Import]
    internal Microsoft.VisualStudio.Text.Operations.ITextUndoHistoryRegistry UndoRegistry { get; set; } = null!;

    public KeyProcessor GetAssociatedProcessor(IWpfTextView wpfTextView) =>
        wpfTextView.Properties.GetOrCreateSingletonProperty(
            () => new VsNeoKeyProcessor(wpfTextView, Gate, CursorSync, UndoRegistry));
}

internal sealed partial class VsNeoKeyProcessor : KeyProcessor
{
    private readonly IWpfTextView _view;
    private readonly IntelliSenseGate _gate;
    private readonly CursorSynchronizer _cursorSync;
    private readonly Microsoft.VisualStudio.Text.Operations.ITextUndoHistoryRegistry _undoRegistry;
    // Pending mapping sequence for the which-key popup, in the exact
    // notation the keys were sent to nvim in; empty means "no prefix live".
    private string _whichKeyPrefix = string.Empty;

    // i_CTRL-O state, UI thread only: armed when <C-o> is sent from insert,
    // disarmed once the hub's mode sequence shows the i -> niI -> i round
    // trip completed. See ResolveCtrlO.
    private bool _ctrlOPending;
    private int _ctrlOArmSequence;
    private int _ctrlOArmTicks;

    public VsNeoKeyProcessor(IWpfTextView view, IntelliSenseGate gate,
        CursorSynchronizer cursorSync,
        Microsoft.VisualStudio.Text.Operations.ITextUndoHistoryRegistry undoRegistry)
    {
        _view = view;
        _gate = gate;
        _cursorSync = cursorSync;
        _undoRegistry = undoRegistry;
    }

    /// <summary>
    /// Read late, never cached. The package loads in the background and the
    /// editor is up long before it finishes, so a processor built at view
    /// creation would capture a null session and keep it for the life of the
    /// view - every key passing through to VS forever, even after nvim is
    /// connected and healthy. Reading the static costs nothing and is not I/O,
    /// so the zero-I/O invariant on this path still holds.
    /// </summary>
    private NvimSession Session => VSNeo_ExtensionPackage.Session;


    /// <summary>
    /// Only document views are ours. The C# Interactive window is an editable
    /// text view too, and it owns its keystrokes outright: with the mode cache
    /// sitting at Normal, every character typed into the REPL was swallowed
    /// and forwarded to nvim. Mirrors attach under this same condition (the
    /// creation listener exports Document-role only), so the two cannot drift.
    /// </summary>
    private bool IsDocumentView =>
        _view.Roles.Contains(PredefinedTextViewRoles.Document);

    /// <summary>
    /// Every reason to hand input straight back to Visual Studio, and not one of
    /// them costs a round trip. Shared so the two entry points cannot drift.
    /// </summary>
    private bool ShouldIntercept(NvimSession session, bool forceVim = false) =>
        InsertKeyDecision.ShouldIntercept(
            IsDocumentView,
            session != null && session.IsReady,
            _view.HasAggregateFocus,
            ForeignFocus(),
            forceVim,
            session != null && session.State.Mode == VimMode.Insert,
            IsIntelliSenseActive());

    /// <summary>
    /// True when focus sits in a Visual Studio control hosted inside the view
    /// rather than on the editor surface itself. HasAggregateFocus cannot tell
    /// the two apart: Roslyn's rename dashboard is a TextBox in an adornment
    /// layer, so the view still reports aggregate focus while it is open.
    ///
    /// It matters because PreviewKeyDown tunnels through the view visual -
    /// where this processor runs - before the focused control ever sees the
    /// key. Intercepting there ate the rename dashboard's Enter and fed nvim
    /// a &lt;CR&gt;, which in normal mode just moves the caret down. TextInput
    /// bubbles instead, so the TextBox already wins for typed characters;
    /// named keys were the ones being stolen.
    /// </summary>
    private bool ForeignFocus()
    {
        var focused = Keyboard.FocusedElement;
        return focused != null && !ReferenceEquals(focused, _view.VisualElement);
    }

    /// <summary>
    /// While a completion list or signature help is open, j and k belong to
    /// that list rather than to nvim, and Escape has to be able to dismiss it.
    /// Callers scope this to insert/replace; in normal mode an open tooltip
    /// does not suspend Vim semantics (see ShouldIntercept).
    /// </summary>
    private bool IsIntelliSenseActive() => _gate != null && _gate.IsActive(_view);
}
