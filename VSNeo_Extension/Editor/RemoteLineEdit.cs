using Microsoft.VisualStudio.Text;

namespace VSNeo_Extension.Editor
{
    /// <summary>
    /// nvim's "lines [first, last) replaced by lines" as the one character span
    /// Visual Studio must replace and the text to put there. VS line i is nvim
    /// line i (the prime sends LineCount lines, empty trailing line included),
    /// so the buffer must read exactly join(lines, newline) afterwards. The line
    /// breaks are the whole difficulty: nvim deals in lines, Visual Studio in a
    /// character range that happens to span them.
    ///
    /// Pure, and linked into the .NET test project: every rule here has a row
    /// in RemoteLineEditTests.
    /// </summary>
    internal static class RemoteLineEdit
    {
        /// <param name="first">First replaced line. May equal <paramref name="last"/>
        /// (a pure insert before it) or LineCount (an append).</param>
        /// <param name="last">One past the last replaced line, already clamped to
        /// [first, LineCount].</param>
        public static void Plan(ITextSnapshot snapshot, int first, int last, string[] lines, string newline,
                                out int start, out int end, out string text)
        {
            int count = snapshot.LineCount;

            if (last < count)
            {
                // Something follows the range: [S_first, S_last) carries the
                // replaced lines and their breaks, and first == last is a pure
                // insert before line last. Either way the new text ends in a
                // break, or the following line joins onto it.
                start = snapshot.GetLineFromLineNumber(first).Start.Position;
                end = snapshot.GetLineFromLineNumber(last).Start.Position;
                text = lines.Length == 0 ? string.Empty : string.Join(newline, lines) + newline;
                return;
            }

            if (first >= count)
            {
                // Append after the last line: the break comes first, since the
                // span sits at the very end of the buffer rather than at a line
                // start. An empty buffer (LineCount 1, Length 0) is the same
                // case - o there must yield one break, two empty lines. It used
                // to be skipped for an empty buffer, and o did nothing.
                start = end = snapshot.Length;
                text = lines.Length == 0 ? string.Empty : newline + string.Join(newline, lines);
                return;
            }

            // The tail [first, count) goes, through the end of the buffer.
            end = snapshot.Length;
            if (lines.Length == 0 && first > 0)
            {
                // Deleting the tail outright must also eat the break that
                // preceded it: ["a","b"] minus "b" is "a", not "a\n" - which
                // Visual Studio reads as ["a",""] while nvim has ["a"], and the
                // drift repair then handed nvim the blank line back. dd on the
                // last line of a file never removed it.
                start = snapshot.GetLineFromLineNumber(first - 1).End.Position;
                text = string.Empty;
                return;
            }

            // Replacing the tail (cc on the last line), or deleting everything
            // from line 0: nothing precedes to join, no trailing break.
            start = snapshot.GetLineFromLineNumber(first).Start.Position;
            text = lines.Length == 0 ? string.Empty : string.Join(newline, lines);
        }
    }
}
