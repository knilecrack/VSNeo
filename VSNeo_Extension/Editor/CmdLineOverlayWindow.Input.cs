using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using VSNeo_Extension.Infrastructure;
using VSNeo_Extension.Nvim;

namespace VSNeo_Extension.Editor;

// The command line itself: runs of styled text, the cursor cell, the caret.
internal static partial class CmdLineOverlayWindow
{
    // The runs and wildmenu rows the overlay is built from, reused across
    // renders: a held Backspace on a long :command re-renders per character,
    // and fresh Run / TextBlock / Border objects per keystroke were the
    // overlay's whole steady-state allocation. Reset in EnsureWindow -
    // recreated controls cannot adopt children parented to the dead ones.
    private static readonly List<Run> InputRuns = new List<Run>();

    private static int s_inputRunCount;

    /// <summary>
    /// Prompt, text, and a block cursor sitting on a character rather than
    /// between two of them - Vim's convention. Without it a long
    /// substitution is edited blind. The command name (s, lua, set) is
    /// drawn in the accent color, the cursor as an accent block.
    /// </summary>
    private static void RenderInput(NvimStateHub state, CmdLineKind kind)
    {
        var content = state.CmdLine ?? string.Empty;
        int cursor = ColumnMapper.ByteToChar(content, state.CmdLinePos);
        if (cursor < 0)
        {
            cursor = 0;
        }

        if (cursor > content.Length)
        {
            cursor = content.Length;
        }

        s_prompt.Text = state.CmdLinePrefix ?? string.Empty;
        s_prompt.Visibility = string.IsNullOrEmpty(s_prompt.Text) ? Visibility.Collapsed : Visibility.Visible;

        s_inputRunCount = 0;
        var name = kind.CommandLength > 0
            ? (kind.CommandStart, kind.CommandStart + kind.CommandLength)
            : (-1, -1);
        // Set while the brush signature holds; Render guarantees it.
        var nameBrush = s_nameBrush!;

        AddText(content, 0, cursor, name, nameBrush);

        // Past the end of the line the cursor has no character to sit on,
        // so it gets a space to occupy instead.
        var under = cursor < content.Length ? content.Substring(cursor, 1) : " ";
        var cursorRun = RentInputRun(under);
        cursorRun.Background = s_cursorBackground;
        cursorRun.Foreground = s_cursorForeground;

        AddText(content, cursor + 1, content.Length, name, nameBrush);

        // Same runs in the same order are already parented from the last
        // render; their Text updates in place. Only a count change
        // rebuilds the list.
        if (s_input.Inlines.Count != s_inputRunCount)
        {
            s_input.Inlines.Clear();
            for (int i = 0; i < s_inputRunCount; i++)
            {
                s_input.Inlines.Add(InputRuns[i]);
            }
        }
    }

    /// <summary>The next pooled run, reset to the plain-text style; the
    /// cursor and the command name apply their styling after the rent.</summary>
    private static Run RentInputRun(string text)
    {
        if (s_inputRunCount == InputRuns.Count)
        {
            InputRuns.Add(new Run());
        }

        var run = InputRuns[s_inputRunCount++];
        run.Text = text;
        run.ClearValue(TextElement.ForegroundProperty);
        run.ClearValue(TextElement.BackgroundProperty);
        run.ClearValue(TextElement.FontWeightProperty);
        return run;
    }

    /// <summary>content[from, to) as runs, the command-name part bold in the accent color.</summary>
    private static void AddText(string content, int from, int to, (int Start, int End) name, Brush nameBrush)
    {
        if (from >= to)
        {
            return;
        }

        int a = Math.Max(from, Math.Min(to, name.Start));
        int b = Math.Max(from, Math.Min(to, name.End));
        if (name.Start < 0 || a >= b)
        {
            RentInputRun(content.Substring(from, to - from));
            return;
        }

        if (a > from)
        {
            RentInputRun(content.Substring(from, a - from));
        }

        var nameRun = RentInputRun(content.Substring(a, b - a));
        nameRun.Foreground = nameBrush;
        nameRun.FontWeight = FontWeights.SemiBold;
        if (to > b)
        {
            RentInputRun(content.Substring(b, to - b));
        }
    }
}
