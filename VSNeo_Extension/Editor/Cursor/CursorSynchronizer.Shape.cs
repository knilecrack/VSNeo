using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.Composition;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Outlining;
using VSNeo_Extension.Infrastructure;
using VSNeo_Extension.Nvim;
using CreationPolicy = System.ComponentModel.Composition.CreationPolicy;


namespace VSNeo_Extension.Editor;

// Mode changes: the caret shape, and brace-match suppression while typing Vim keys.
internal sealed partial class CursorSynchronizer
{
    // Values displaced by the brace-match suppression, per format and key, so
    // leaving visual mode restores the view exactly as it was. UI thread only.
    //private readonly List<(string Format, string Key, bool Existed, object? Value)> _braceMatchSaved = new();
    private readonly List<BraceMatchSaved> _braceMatchSaved = [];

    // The names VS's delimiter highlight is known by. The format name is not
    // part of any public contract and both casings have shipped, so every
    // candidate is overridden - keys set on a name nothing reads are simply
    // never resolved.
    private static readonly string[] BraceMatchFormatNames =
    {
        "Brace Matching (Rectangle)",
        "brace matching (Rectangle)",
        "Brace Matching (Highlight)",
        "brace matching (Highlight)",
    };

    // Fill and border both go transparent: the rectangle variant draws its
    // border from the foreground, the highlight variant from the background,
    // and the rendering layer may consult either the color or the brush key.
    private static readonly KeyValuePair<string, object>[] BraceMatchOverrides =
    {
        new("BackgroundColor", Colors.Transparent),
        new("BackgroundBrush", Brushes.Transparent),
        new("ForegroundColor", Colors.Transparent),
        new("BorderBrush", Brushes.Transparent),
    };

    /// <summary>
    /// Called on the RPC read thread, once per nvim redraw cycle. Records the
    /// position and schedules at most one hop to the UI thread; a burst of
    /// motions collapses into a single caret move with the latest value.
    /// </summary>
    /// <summary>Called on the RPC read thread when nvim's mode changes.</summary>
    private void OnModeChanged(VimMode mode)
    {
        if (mode == VimMode.Insert || mode == VimMode.Replace)
        {
            Volatile.Write(ref _applyOnceInInsert, 1);
        }

        var dispatcher = _dispatcher;
        if (dispatcher == null)
        {
            return;
        }

#pragma warning disable VSTHRD001
        _ = dispatcher.BeginInvoke(Infrastructure.UiPriority.KeyResponse, new Action(() =>
        {
            // Runs on the UI thread via the dispatcher hop; the analyzer cannot
            // prove that from inside the lambda, so assert it.
            ThreadHelper.ThrowIfNotOnUIThread();

            ApplyCaretShape(mode);
            UpdateBraceMatchSuppression(mode);

            // Redraw the selection on the mode change itself, not only when the
            // cursor moves. Pressing v sets the anchor and leaves the cursor
            // exactly where it is, so CursorMoved never fires and nothing would
            // appear selected until the first motion. Leaving visual is the same
            // in reverse: the selection has to be cleared.
            ApplyPending(measure: false);
        }));
#pragma warning restore VSTHRD001
    }

    /// <summary>
    /// A block caret in the modes where Vim has one, a thin caret in insert.
    ///
    /// This is not decoration, it is what makes the cursor model legible. Vim's
    /// normal-mode cursor sits *on* a character and cannot go past the last one,
    /// so the rightmost position on a line is the final character, not the empty
    /// space after it. Visual Studio's caret sits *between* characters, so nvim
    /// reporting "on the last character" drew a thin line to the left of it -
    /// looking permanently one column short, and making the end of the line
    /// impossible to reach. Same position, different convention.
    ///
    /// Overwrite mode is how the VS editor draws a block, and it covers the
    /// character the caret is on, which is exactly Vim's cursor. It also changes
    /// what typing does, which is harmless here only because normal mode does not
    /// let keystrokes through to the editor - and it is switched off before
    /// insert mode, where they do.
    ///
    /// Visual mode is the exception: the caret sits at the selection's
    /// exclusive end, where Visual Studio renders no block at all, so the
    /// overwrite block is off there and VisualBlockCaretAdornment draws the
    /// block over nvim's cursor character instead.
    /// </summary>
    private void ApplyCaretShape(VimMode mode)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var view = _activeView;
        if (view == null || view.IsClosed)
        {
            return;
        }

        bool block = VSNeoSettings.Enabled && mode is (VimMode.Normal
                  or VimMode.OperatorPending
                  or VimMode.Terminal);

        try
        {
            view.Options.SetOptionValue(DefaultTextViewOptions.OverwriteModeId, block);
        }
        catch
        {
            // Caret shape is cosmetic; never let it break the session.
        }
    }

    /// <summary>
    /// Suppresses Visual Studio's automatic delimiter highlighting for the
    /// active view while visual mode is active, and restores it elsewhere.
    ///
    /// The caret in visual mode sits on the character under nvim's cursor
    /// (ApplySelection stops the span there so the block marks the inclusive
    /// end). When that character is a bracket, delimiter highlighting boxes
    /// it and its match - a rectangle fighting the block for the same cell,
    /// plus a second box somewhere outside the selection. Neither helps
    /// read the selection, so the highlight goes while visual mode is on.
    ///
    /// Done through the per-view format map so the user's global Fonts and
    /// Colors settings are never touched.
    /// </summary>
    private void UpdateBraceMatchSuppression(VimMode mode)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (mode != VimMode.Visual)
        {
            RestoreBraceMatchFormats();
            return;
        }

        if (_braceMatchSaved.Count != 0)
        {
            return;   // already suppressed
        }

        var view = _activeView;
        if (view == null || view.IsClosed || _formatMapService == null)
        {
            return;
        }

        try
        {
            var formatMap = _formatMapService.GetEditorFormatMap(view);
            formatMap.BeginBatchUpdate();
            try
            {
                foreach (var name in BraceMatchFormatNames)
                {
                    var properties = formatMap.GetProperties(name);
                    foreach (var pair in BraceMatchOverrides)
                    {
                        bool existed = properties.Contains(pair.Key);
                        _braceMatchSaved.Add(new BraceMatchSaved(name, pair.Key, existed, existed ? properties[pair.Key] : null));
                        properties[pair.Key] = pair.Value;
                    }
                }
            }
            finally
            {
                formatMap.EndBatchUpdate();
            }
        }
        catch
        {
            // Cosmetic only; never let it break the session.
            _braceMatchSaved.Clear();
        }
    }

    private void RestoreBraceMatchFormats()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (_braceMatchSaved.Count == 0)
        {
            return;
        }

        var saved = _braceMatchSaved.ToArray();
        _braceMatchSaved.Clear();

        var view = _activeView;
        if (view == null || view.IsClosed || _formatMapService == null)
        {
            return;
        }

        try
        {
            var formatMap = _formatMapService.GetEditorFormatMap(view);
            formatMap.BeginBatchUpdate();
            try
            {
                foreach (var braceMatchSaved in saved)
                {
                    // Format, not Key: the properties belong to the format
                    // definition ("brace matching"), and looking them up by
                    // the property name created junk entries named
                    // "BackgroundColor" while the real colors stayed suppressed.
                    var properties = formatMap.GetProperties(braceMatchSaved.Format);
                    if (braceMatchSaved.Existed)
                    {
                        properties[braceMatchSaved.Key] = braceMatchSaved.Value!;
                    }
                    else
                    {
                        properties.Remove(braceMatchSaved.Key);
                    }
                }
            }
            finally
            {
                formatMap.EndBatchUpdate();
            }
        }
        catch
        {
            // Cosmetic only; never let it break the session.
        }
    }
}

public readonly struct BraceMatchSaved(string format, string key, bool existed, object? value)
{
    public string Format { get; } = format;
    public string Key { get; } = key;
    public bool Existed { get; } = existed;
    public object? Value { get; } = value;
}
