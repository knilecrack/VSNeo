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

        /// <summary>
        /// Narrows a planned edit to the characters that actually change: the
        /// common prefix and suffix of the old and new text are left in place.
        /// The buffer reads the same afterwards; what differs is everything
        /// that tracks positions - a whole-line replace moved the caret to the
        /// line's end, and collapsed the completion list's tracking span so the
        /// list dismissed itself one character after it opened. Never splits a
        /// CRLF pair or a surrogate pair.
        /// </summary>
        public static void Narrow(string oldText, ref int start, ref int end, ref string text)
        {
            int oldLen = oldText.Length, newLen = text.Length;
            int max = oldLen < newLen ? oldLen : newLen;

            int prefix = 0;
            while (prefix < max && oldText[prefix] == text[prefix]) prefix++;
            while (prefix > 0 && (SplitsPair(oldText, prefix) || SplitsPair(text, prefix))) prefix--;

            int suffix = 0;
            while (suffix < max - prefix && oldText[oldLen - 1 - suffix] == text[newLen - 1 - suffix]) suffix++;
            while (suffix > 0 && (SplitsPair(oldText, oldLen - suffix) || SplitsPair(text, newLen - suffix))) suffix--;

            start += prefix;
            end -= suffix;
            text = text.Substring(prefix, newLen - prefix - suffix);
        }

        /// <summary>A cut before index <paramref name="at"/> separates a CRLF or a surrogate pair.</summary>
        private static bool SplitsPair(string s, int at)
        {
            if (at <= 0 || at >= s.Length) return false;
            return (s[at - 1] == '\r' && s[at] == '\n')
                   || (char.IsHighSurrogate(s[at - 1]) && char.IsLowSurrogate(s[at]));
        }
    }
}
