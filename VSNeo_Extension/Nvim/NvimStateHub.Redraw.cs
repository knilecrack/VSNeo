using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace VSNeo_Extension.Nvim;

// The redraw stream: which events are ours, and the state push they carry.
internal sealed partial class NvimStateHub
{
    /// <summary>
    /// The redraw events <see cref="OnNotification"/> handles below. The
    /// stream reader skips every other batch (linegrid cell runs, viewport
    /// reports, highlight definitions) without decoding it, so a name added
    /// to the switch must be added here too or its events never materialize.
    /// </summary>
    internal static bool IsHandledRedrawEvent(string name)
    {
        switch (name)
        {
            case "cmdline_show":
            case "cmdline_pos":
            case "cmdline_hide":
            case "popupmenu_show":
            case "popupmenu_select":
            case "popupmenu_hide":
            case "msg_show":
            case "msg_showmode":
            case "msg_showcmd":
            case "msg_clear":
            case "msg_history_show":
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// The byte-twin of <see cref="IsHandledRedrawEvent"/>: matches a batch
    /// name's raw UTF-8 bytes against the handled set (all of which are
    /// pure ASCII) and hands back the canonical string constant on a hit,
    /// null on a miss. The stream reader uses it to recognize batches
    /// without decoding a string it would immediately throw away.
    /// </summary>
    internal static string? MatchHandledRedrawEvent(byte[] buf, int offset, int length)
    {
        switch (length)
        {
            case 8:
                return MatchName(buf, offset, "msg_show") ? "msg_show" : null;
            case 9:
                return MatchName(buf, offset, "msg_clear") ? "msg_clear" : null;
            case 11:
                if (MatchName(buf, offset, "cmdline_pos"))
                {
                    return "cmdline_pos";
                }

                if (MatchName(buf, offset, "msg_showcmd"))
                {
                    return "msg_showcmd";
                }

                return null;
            case 12:
                if (MatchName(buf, offset, "cmdline_show"))
                {
                    return "cmdline_show";
                }

                if (MatchName(buf, offset, "cmdline_hide"))
                {
                    return "cmdline_hide";
                }

                if (MatchName(buf, offset, "msg_showmode"))
                {
                    return "msg_showmode";
                }

                return null;
            case 13:
                if (MatchName(buf, offset, "popupmenu_show"))
                {
                    return "popupmenu_show";
                }

                if (MatchName(buf, offset, "popupmenu_hide"))
                {
                    return "popupmenu_hide";
                }

                return null;
            case 15:
                return MatchName(buf, offset, "popupmenu_select") ? "popupmenu_select" : null;
            case 16:
                return MatchName(buf, offset, "msg_history_show") ? "msg_history_show" : null;
            default:
                return null;
        }
    }

    private static bool MatchName(byte[] buf, int offset, string name)
    {
        for (int i = 0; i < name.Length; i++)
        {
            if (buf[offset + i] != (byte)name[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Called from the RPC read thread. Keep it allocation-light and non-blocking.</summary>
    public void OnNotification(string method, object[] args)
    {
        // Mode and cursor arrive from the Lua companion, which reports what Vim
        // is actually doing. The redraw stream is left to describe the one thing
        // it is the only source for: the command line. A switch, not a chain:
        // redraw is the most frequent notification on the wire and was the one
        // that failed every comparison before matching.
        switch (method)
        {
            case "redraw":
                HandleRedraw(args);
                return;
            case "vsneo_state":
                HandleState(args);
                return;
            case "vsneo_buf_enter":
                HandleBufEnter(args);
                return;
            case "vsneo_marks":
                _marks.HandleMarks(args);
                return;
            case "vsneo_keymaps":
                HandleKeymaps(args);
                return;
            case "vsneo_imaps":
                HandleImaps(args);
                return;
            case "vsneo_recording":
                HandleRecording(args);
                return;
            case "vsneo_search_count":
                _search.HandleSearchCount(args);
                return;
            case "vsneo_search_matches":
                _search.HandleSearchMatches(args);
                return;
            case "vsneo_highlights":
                _search.HandleHighlights(args);
                return;
            case "vsneo_linenumbers":
                HandleLineNumbers(args);
                return;
            case "vsneo_scrolloff":
                Scrolloff = args != null && args.Length > 0 ? Math.Max(0, ToInt(args[0])) : 0;
                return;
            case "vsneo_cursor_animation":
                HandleCursorAnimation(args);
                return;
            case "vsneo_cursor_style":
                HandleCursorStyle(args);
                return;
            case "vsneo_yank":
                _search.HandleYank(args);
                return;
            case "vsneo_undo_flash":
                UndoFlashEnabled = args != null && args.Length > 0 && ToInt(args[0]) != 0;
                return;
            case "vsneo_log":
                if (args != null && args.Length > 0)
                {
                    Infrastructure.Log.Write("nvim: " + AsString(args[0]));
                }

                return;
            case "vsneo_esc_closes_popup":
                EscClosesPopup = args != null && args.Length > 0 && ToInt(args[0]) != 0;
                return;
            case "vsneo_overlay_active":
                HandleOverlayActive(args);
                return;
            case "vsneo_overlay_labels":
                HandleOverlayLabels(args);
                return;
            case "vsneo_folds_changed":
                HandleFoldsChanged(args);
                return;
            case "vsneo_fold_create":
                HandleFoldCreate(args);
                return;
        }
    }

    private void HandleRedraw(object[] args) => _prompt.HandleRedraw(args);

    /// <summary>
    /// One notification carrying everything the key path and the caret need:
    /// [mode, line, byteColumn]. Mode is Vim's own short code from mode(), and
    /// line is already 0-based - the companion converts it.
    ///
    /// This replaced two pieces of inference. The cursor used to be lifted out of
    /// win_viewport, where it appears as a byproduct of describing the viewport,
    /// and mode out of mode_change, whose names describe cursor *shape*. Both
    /// arrived on the redraw cycle, so both were as current as the last repaint
    /// rather than as current as Vim.
    /// </summary>
    private void HandleState(object[] args)
    {
        if (args == null || args.Length < 3)
        {
            return;
        }

        HandleStateCore(
            AsString(args[0]),
            ToInt(args[1]),
            ToInt(args[2]),
            args.Length > 3 ? ToInt(args[3]) : -1,
            args.Length > 4 ? ToInt(args[4]) : -1,
            args.Length > 5 ? ToInt(args[5]) : -1,
            args.Length > 6 && args[6] is bool b && b,
            args.Length > 7 && args[7] is bool syn && syn);
    }

    /// <summary>
    /// The zero-allocation twin of <see cref="HandleState"/>: the stream
    /// reader's fast path hands the push over as a struct, skipping the
    /// frame and args arrays entirely. Same wire shape, same handling.
    /// </summary>
    public void OnStatePush(StatePush p) =>
        HandleStateCore(p.Mode, p.Line, p.ByteColumn, p.TopLine,
            p.AnchorLine, p.AnchorColumn, p.BlockToEol, p.Synthetic);

    private void HandleStateCore(string raw, int line, int col, int topLine,
        int anchorLine, int anchorColumn, bool blockToEol, bool synthetic)
    {
        // The far end of a visual selection, and which flavour of visual it is.
        // Charwise, linewise and blockwise select completely different regions
        // from the same pair of positions, so the distinction has to survive.
        VisualAnchorLine = anchorLine;
        VisualAnchorColumn = anchorColumn;
        VisualKind = string.IsNullOrEmpty(raw) ? '\0' : raw[0];

        // $ in blockwise visual reaches the end of every line in the block.
        // The companion reads that off curswant == v:maxcol; without the flag
        // the extension can only draw the corner-to-corner rectangle.
        VisualBlockToEol = blockToEol;

        // The synthetic flag marks viewport bookkeeping: the companion
        // clamped nvim's cursor into the window while Visual Studio's caret
        // is scrolled off it (nvim windows cannot hide their cursor, and
        // H/M/L must compute against what is on screen). That position is
        // cached - it is where nvim's cursor really is - but never raised:
        // the caret here stays where the user left it.

        var mode = ParseShort(raw);

        // Modes beginning with 'r' are prompts: hit-enter, "-- more --", or a
        // :confirm query. nvim has stopped and is waiting for an answer nobody
        // can see, because nothing here renders one. Whatever caused it is a bug
        // to fix at the source rather than to key off, so say so loudly.
        if (!string.IsNullOrEmpty(raw) && raw[0] == 'r')
        {
            Infrastructure.Log.Write(
                "nvim is BLOCKED at a prompt (mode \"" + raw + "\") and is ignoring input");
        }

        _pushedMode = (int)mode;
        bool modeChanged = PublishMode(raw);

        // Scrolling is reported separately from the cursor because zz, zt, zb and
        // the <C-e>/<C-y> pair change only what is visible. Handled before the
        // early return below, which a pure scroll would otherwise take.
        if (topLine >= 0 && Interlocked.Exchange(ref _topLine, topLine) != topLine)
        {
            ViewportScrolled?.Invoke(topLine);
        }

        if (line < 0 || col < 0)
        {
            return;
        }

        long packed = ((long)line << 32) | (uint)col;
        if (synthetic)
        {
            Interlocked.Exchange(ref _cursor, packed);
            return;
        }

        // A mode change raises even an unchanged cursor. Mode and position
        // are one push, and CursorSynchronizer applies the position paired
        // with the move into insert exactly once - with the dedupe winning
        // here, that application used whatever position was last reported,
        // which a dropped or clamped report could have left stale (the
        // "caret jumps when I press i" bug).
        long previous = Interlocked.Exchange(ref _cursor, packed);
        if (previous == packed && !modeChanged)
        {
            return;
        }

        CursorMoved?.Invoke(line, col);
    }

    /// <summary>
    /// Publishes the effective mode if it changed: the pushed mode, with
    /// cmdline visibility from the redraw stream winning while a command
    /// line is open. Called from HandleState (a push arrived) and from
    /// SetCmdLine (the command line opened or closed); either can be the
    /// one that changes the answer, depending on which stream lands first.
    /// Returns true when the effective mode actually changed.
    /// </summary>
    private bool PublishMode(string? raw)
    {
        var effective = CmdLine != null ? VimMode.CmdLine : (VimMode)_pushedMode;
        if ((VimMode)_mode == effective)
        {
            return false;
        }

        Infrastructure.Log.Write(
            "mode: " + (raw == null ? string.Empty : "\"" + raw + "\" ") + "-> " + effective);
        _mode = (int)effective;
        Interlocked.Increment(ref _modeSequence);
        ModeChanged?.Invoke(effective);
        return true;
    }

    /// <summary>
    /// Vim's own mode codes, as returned by mode(). These are not the redraw
    /// stream's names: "n" not "normal", and the distinctions that matter are in
    /// the second character - "no" is operator-pending, which is emphatically not
    /// normal as far as the key path is concerned.
    /// </summary>
    private static VimMode ParseShort(string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return VimMode.Unknown;
        }

        switch (s[0])
        {
            case 'n':
                // "no", "nov", "noV", "no^V" are all operator-pending.
                return s.Length > 1 && s[1] == 'o' ? VimMode.OperatorPending : VimMode.Normal;

            case 'i':
                return VimMode.Insert;
            case 'R':
                return VimMode.Replace;

            // Visual and Select, charwise / linewise / blockwise. Select behaves
            // like Visual for our purposes: nvim owns it and we swallow keys.
            case 'v':
            case 'V':
            case '\x16':
            case 's':
            case 'S':
            case '\x13':
                return VimMode.Visual;

            case 'c':
                return VimMode.CmdLine;
            case 't':
                return VimMode.Terminal;

            default:
                return VimMode.Unknown;
        }
    }

    private void HandleBufEnter(object[] args)
    {
        var path = NormalizePath(args != null && args.Length > 0 ? AsString(args[0]) : null);
        if (path == null)
        {
            return;
        }

        if (string.Equals(CurrentBufferPath, path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        CurrentBufferPath = path;
        BufferSwitched?.Invoke(path);
    }
}
