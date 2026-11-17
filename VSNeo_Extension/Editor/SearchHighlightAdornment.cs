using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Utilities;
using VSNeo_Extension.Infrastructure;
using VSNeo_Extension.Nvim;

namespace VSNeo_Extension.Editor;

/// <summary>
/// Draws Vim's hlsearch matches behind the text.
///
/// nvim computes the matches and sends their positions; this margin turns them
/// into background rectangles in the Visual Studio editor. It deliberately does
/// not reimplement Vim's regex engine in C#.
///
/// While the cursor is on a match it also draws a [current/total] chip at the
/// end of that line, nvim-hlslens style. The count needs no round trip: the
/// match list is already here, sorted, so the current match's index in it is
/// the answer.
/// </summary>
[Export(typeof(IWpfTextViewCreationListener))]
[ContentType("text")]
[TextViewRole(PredefinedTextViewRoles.Document)]
internal sealed class SearchHighlightAdornmentProvider : IWpfTextViewCreationListener
{
    [Export(typeof(AdornmentLayerDefinition))]
    [Name("VSNeoSearchHighlight")]
    [Order(After = PredefinedAdornmentLayers.Selection, Before = PredefinedAdornmentLayers.Text)]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    // MEF populates this through the Export above; nothing in code assigns it.
    internal AdornmentLayerDefinition _layerDefinition = null!;

    public void TextViewCreated(IWpfTextView textView)
    {
        textView.Properties.GetOrCreateSingletonProperty(
            () => new SearchHighlightAdornment(textView));
    }
}

internal sealed partial class SearchHighlightAdornment
{
    private const string LayerName = "VSNeoSearchHighlight";

    private readonly IAdornmentLayer _layer;
    private readonly IWpfTextView _view;
    // Assigned by BuildBrushes(), which the constructor calls; the compiler
    // cannot see through the method call.
    private Brush _searchBrush = null!;
    private Brush _currentBrush = null!;
    private bool _disposed;

    // Hub events arrive on the RPC read thread and reach every open view;
    // only the focused one has anything to draw. Tracked here (set from the
    // view's focus events) so unfocused views bail before paying a
    // dispatcher hop per event. volatile: read on the RPC thread.
    private volatile bool _focused;

    public SearchHighlightAdornment(IWpfTextView view)
    {
        _view = view;
        _layer = view.GetAdornmentLayer(LayerName);
        _focused = view.HasAggregateFocus;

        BuildBrushes();

        Subscribe();
        view.LayoutChanged += OnLayoutChanged;
        view.GotAggregateFocus += OnGotFocus;
        view.LostAggregateFocus += OnLostFocus;
        view.Closed += OnClosed;
    }

    /// <summary>
    /// Colors come from nvim's own highlight groups, so ':hi Search
    /// guibg=...' in ~/.vsneorc really changes what Visual Studio draws.
    /// The fallbacks keep Vim's look: translucent yellow for Search, a
    /// stronger orange for the current match (CurSearch/IncSearch).
    /// </summary>
    private void BuildBrushes()
    {
        var state = VSNeo_ExtensionPackage.Session?.State;
        _searchBrush = MakeBrush(state == null ? -1 : state.SearchColor, 0x50, 0xFFD700);
        _currentBrush = MakeBrush(state == null ? -1 : state.CurrentMatchColor, 0x70, 0xFF9E40);
    }

    private static Brush MakeBrush(int rgb, byte fallbackAlpha, int fallbackRgb)
    {
        // nvim's bg is opaque; on text it has to stay translucent. 0xB0 reads
        // as the group color without drowning the foreground.
        var brush = rgb >= 0
            ? new SolidColorBrush(Color.FromArgb(0xB0,
                (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb))
            : new SolidColorBrush(Color.FromArgb(fallbackAlpha,
                (byte)(fallbackRgb >> 16), (byte)(fallbackRgb >> 8), (byte)fallbackRgb));
        brush.Freeze();
        return brush;
    }

    // Null until Subscribe() finds a live session; checked at every use.
    private NvimStateHub? _subscribedTo;
    private int _readyHooked;

    private void Subscribe()
    {
        // Posted from the ready broadcast: the view can close before the
        // post runs, and subscribing then held it for the whole session.
        if (_disposed)
        {
            return;
        }

        var session = VSNeo_ExtensionPackage.Session;
        if (session == null)
        {
            // Created with the startup document, before the package loaded:
            // wait for the ready broadcast rather than staying deaf.
            if (Interlocked.Exchange(ref _readyHooked, 1) == 0)
            {
                VSNeo_ExtensionPackage.SessionReadyChanged += OnSessionReady;
            }

            return;
        }

        if (ReferenceEquals(_subscribedTo, session.State))
        {
            return;
        }

        if (_subscribedTo != null)
        {
            _subscribedTo.SearchMatchesChanged -= OnMatchesChanged;
            _subscribedTo.HighlightsChanged -= OnHighlightsChanged;
            _subscribedTo.CursorMoved -= OnCursorMoved;
            _subscribedTo.ModeChanged -= OnModeChanged;
        }

        session.State.SearchMatchesChanged += OnMatchesChanged;
        session.State.HighlightsChanged += OnHighlightsChanged;
        session.State.CursorMoved += OnCursorMoved;
        session.State.ModeChanged += OnModeChanged;
        _subscribedTo = session.State;
    }

    private void OnSessionReady(bool ready)
    {
        if (!ready)
        {
            return;
        }

        var dispatcher = _view.VisualElement.Dispatcher;
        if (dispatcher == null)
        {
            return;
        }

#pragma warning disable VSTHRD001
        _ = dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Input,
            new Action(() =>
            {
                BuildBrushes();
                Subscribe();
                BeginRedraw();
            }));
#pragma warning restore VSTHRD001
    }

    private void OnMatchesChanged() => BeginRedraw();

    private void OnCursorMoved(int line, int byteColumn)
    {
        if (!_focused)
        {
            return;
        }

        var session = VSNeo_ExtensionPackage.Session;
        var matches = session?.State.SearchMatches;

        int curLine = -1, curStart = -1;
        if (matches != null)
        {
            // Same "on the match" rule as Redraw, including the inclusive
            // end while a / or ? cmdline is open.
            bool searchTyping = session!.State.CmdLinePrefix == "/"
                || session.State.CmdLinePrefix == "?";
            foreach (var m in matches)
            {
                if (m.Line < line)
                {
                    continue;
                }

                if (m.Line > line)
                {
                    break; // matches arrive sorted by line
                }

                if (m.StartByte <= byteColumn
                    && (byteColumn < m.EndByte || (searchTyping && byteColumn == m.EndByte)))
                {
                    curLine = m.Line;
                    curStart = m.StartByte;
                    break;
                }
            }
        }

        if (curLine == _drawnCurrentLine && curStart == _drawnCurrentStart)
        {
            return;
        }

        BeginRedraw();
    }

    // The counter hides in insert mode and comes back on the way out; a
    // mode change without a cursor move would otherwise leave it stale.
    private void OnModeChanged(VimMode mode)
    {
        if (_countShown || mode != VimMode.Insert)
        {
            BeginRedraw();
        }
    }

    private void OnHighlightsChanged()
    {
        BuildBrushes();
        BeginRedraw();
    }

    // Runs on the UI thread (view focus events), so Redraw directly.
    // Focus loss clears the layer; focus gain draws the current matches.
    // The flag is set from the event itself, not re-read from
    // HasAggregateFocus: that property is not reliable inside the events,
    // and a stale false there silenced the highlights for good.
    private void OnGotFocus(object sender, EventArgs e)
    {
        _focused = true;
        Redraw();
    }

    private void OnLostFocus(object sender, EventArgs e)
    {
        _focused = false;
        Redraw();
    }

    private int _redrawPending;

    private void BeginRedraw()
    {
        // Unfocused views have nothing to draw; the focus handler repaints
        // on the way back in.
        if (!_focused)
        {
            return;
        }

        var dispatcher = _view.VisualElement.Dispatcher;
        if (dispatcher == null)
        {
            return;
        }

        // One queued redraw at a time: holding j with matches on screen
        // used to stack one full rebuild per cursor move.
        if (Interlocked.Exchange(ref _redrawPending, 1) == 1)
        {
            return;
        }

#pragma warning disable VSTHRD001
        // The delegate is allocated once: a held key with matches on screen
        // queues this per cursor move.
        var redraw = _redrawAction ??= new Action(() =>
        {
            Volatile.Write(ref _redrawPending, 0);
            Redraw();
        });
        _ = dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, redraw);
#pragma warning restore VSTHRD001
    }

    private Action? _redrawAction;

    /// <summary>
    /// Layout changes are incremental. Adornments are TextRelative, so the
    /// layer itself moves them on scroll and drops the ones on lines it
    /// reformats; only NewOrReformattedLines need drawing. This used to be a
    /// full rebuild - RemoveAllAdornments and a fresh Image per visible
    /// match - on every layout, and typing lays out the current line on
    /// every keystroke.
    /// </summary>
    private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
    {
        using var perf = Infrastructure.Perf.Time("SearchHighlightAdornment.OnLayoutChanged");
        if (_disposed)
        {
            return;
        }

        try
        {
            if (!TryGetDrawState(out var session, out var matches))
            {
                if (_layer.Elements.Count > 0)
                {
                    _layer.RemoveAllAdornments();
                }

                _drawnCurrentLine = _drawnCurrentStart = _drawnCurrentIndex = -1;
                _countShown = false;
                return;
            }

            bool countLost = false;
            int foundBefore = _drawnCurrentIndex;
            foreach (var line in e.NewOrReformattedLines)
            {
                // A reformatted line lost its adornments, the current
                // match's and the [n/N] chip included; DrawLine records the
                // match again if it is still there, and the chip follows.
                if (_drawnCurrentLine >= 0 && LineHolds(line, _drawnCurrentLine))
                {
                    _drawnCurrentLine = _drawnCurrentStart = _drawnCurrentIndex = -1;
                    countLost = true;
                }

                DrawLine(line, session, matches);
            }

            // The chip is only redrawn when its line was (or when the
            // current match just scrolled into view): typing elsewhere
            // leaves it alone, like every other highlight.
            if (countLost || (foundBefore < 0 && _drawnCurrentIndex >= 0))
            {
                ShowCount(session, matches);
            }
        }
        catch (Exception ex)
        {
            // An adornment must never take the editor down with it.
            Infrastructure.Log.Write("search highlight layout update failed", ex);
        }
    }

    private void OnClosed(object sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_subscribedTo != null)
        {
            _subscribedTo.SearchMatchesChanged -= OnMatchesChanged;
            _subscribedTo.HighlightsChanged -= OnHighlightsChanged;
            _subscribedTo.CursorMoved -= OnCursorMoved;
            _subscribedTo.ModeChanged -= OnModeChanged;
        }

        if (Interlocked.Exchange(ref _readyHooked, 0) == 1)
        {
            VSNeo_ExtensionPackage.SessionReadyChanged -= OnSessionReady;
        }

        _view.LayoutChanged -= OnLayoutChanged;
        _view.GotAggregateFocus -= OnGotFocus;
        _view.LostAggregateFocus -= OnLostFocus;
        _view.Closed -= OnClosed;
    }
}
