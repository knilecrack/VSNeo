using System.Collections.Generic;
using System.Linq;

namespace VSNeo_Extension.Editor
{
    /// <summary>
    /// The syntax text objects and motions over Visual Studio's code model -
    /// the fallback for languages Roslyn does not own, C++ above all. The code
    /// model (EnvDTE FileCodeModel) lists a file's functions and types with
    /// their ranges, and that is all this needs. Pure: the text and the
    /// elements in, a target out, the same operations as SyntaxTargets, so the
    /// .NET tests run it without Visual Studio.
    /// </summary>
    internal static class CodeModelTargets
    {
        internal enum Kind { Function, Type }

        /// <summary>A function or type: [Start, End) in characters of the text.</summary>
        internal readonly struct Element
        {
            public Element(Kind kind, int start, int end)
            {
                Kind = kind;
                Start = start;
                End = end;
            }
            public Kind Kind { get; }
            public int Start { get; }
            public int End { get; }
        }

        public static SyntaxTarget? Find(string text, IReadOnlyList<Element> elements, int position, string op, int count)
        {
            if (text == null || elements == null) return null;
            if (count < 1) count = 1;
            position = position < 0 ? 0 : (position > text.Length ? text.Length : position);

            switch (op)
            {
                case "function_outer": return Outer(text, Enclosing(text, elements, position, Kind.Function), count);
                case "function_inner": return Inner(text, Enclosing(text, elements, position, Kind.Function), count);
                case "class_outer": return Outer(text, Enclosing(text, elements, position, Kind.Type), count);
                case "class_inner": return Inner(text, Enclosing(text, elements, position, Kind.Type), count);
                case "function_next_start": return Next(Starts(elements, Kind.Function), position, count);
                case "function_prev_start": return Prev(Starts(elements, Kind.Function), position, count);
                case "function_next_end": return Next(Ends(elements, Kind.Function), position, count);
                case "function_prev_end": return Prev(Ends(elements, Kind.Function), position, count);
                case "class_next_start": return Next(Starts(elements, Kind.Type), position, count);
                case "class_prev_start": return Prev(Starts(elements, Kind.Type), position, count);
                default: return null;
            }
        }

        /// <summary>The elements of a kind around the position, innermost
        /// (smallest) first. Indentation before a declaration on its first
        /// line counts as inside it, as in SyntaxTargets.</summary>
        private static List<Element> Enclosing(string text, IReadOnlyList<Element> elements, int position, Kind kind)
        {
            int lineStart = LineStart(text, position);
            int firstNonBlank = lineStart;
            while (firstNonBlank < text.Length && (text[firstNonBlank] == ' ' || text[firstNonBlank] == '\t')) firstNonBlank++;
            int probe = position < firstNonBlank ? firstNonBlank : position;

            return elements
                .Where(e => e.Kind == kind && e.Start <= probe && probe < e.End)
                .OrderBy(e => e.End - e.Start)
                .ToList();
        }

        private static SyntaxTarget? Outer(string text, List<Element> around, int depth)
        {
            if (around.Count < depth) return null;
            var e = around[depth - 1];
            return Range(text, e.Start, e.End);
        }

        /// <summary>
        /// The body is the last top-level brace pair of the declaration, never
        /// simply its first '{': in `C::C() : value{1} { Work(); }` that one
        /// belongs to the initializer, and dif took `1} { Work();`. Braces in
        /// comments, strings and character literals are not braces.
        /// </summary>
        // ponytail: raw string literals (R"(...)") are read as plain strings; a
        // lexer if a brace inside one ever mis-pairs a body.
        private static bool BodyBraces(string text, int from, int to, out int open, out int close)
        {
            open = close = -1;
            int depth = 0, pending = -1;
            for (int i = from; i < to; i++)
            {
                char c = text[i];
                if (c == '/' && i + 1 < to && text[i + 1] == '/')
                {
                    while (i < to && text[i] != '\n') i++;
                }
                else if (c == '/' && i + 1 < to && text[i + 1] == '*')
                {
                    int end = text.IndexOf("*/", i + 2, System.StringComparison.Ordinal);
                    i = end < 0 || end >= to ? to : end + 1;
                }
                else if (c == '"' || (c == '\'' && !(i > from && char.IsLetterOrDigit(text[i - 1]))))
                {
                    // Not 1'000: a quote after a digit is a separator.
                    for (i++; i < to && text[i] != c && text[i] != '\n'; i++)
                        if (text[i] == '\\') i++;
                }
                else if (c == '{')
                {
                    if (depth++ == 0) pending = i;
                }
                else if (c == '}' && depth > 0 && --depth == 0)
                {
                    open = pending;
                    close = i;
                }
            }
            return open >= 0;
        }

        /// <summary>Between the element's body braces. A declaration with no
        /// body (a prototype) is no target.</summary>
        private static SyntaxTarget? Inner(string text, List<Element> around, int depth)
        {
            if (around.Count < depth) return null;
            var e = around[depth - 1];
            if (!BodyBraces(text, e.Start, e.End, out int open, out int close)) return null;

            // Linewise only when each brace stands alone at its end of the body,
            // as SyntaxTargets.BetweenBraces decides: `{ a();` over `b(); }` is
            // the characters between them, not the lines.
            int openLine = LineOf(text, open), closeLine = LineOf(text, close);
            if (closeLine > openLine
                && string.IsNullOrWhiteSpace(text.Substring(open + 1, LineEnd(text, open) - (open + 1)))
                && string.IsNullOrWhiteSpace(text.Substring(LineStart(text, close), close - LineStart(text, close))))
            {
                if (closeLine - openLine == 1) return null;
                int start = LineStartAfter(text, open);
                int end = LineStart(text, close);
                // The last inner line, without its line break - one break only:
                // empty lines before the closing brace are part of the body.
                if (end > start && text[end - 1] == '\n') end--;
                if (end > start && text[end - 1] == '\r') end--;
                return new SyntaxTarget(start, end, linewise: true);
            }

            int s = open + 1, t = close;
            while (s < t && char.IsWhiteSpace(text[s])) s++;
            while (t > s && char.IsWhiteSpace(text[t - 1])) t--;
            return t > s ? new SyntaxTarget(s, t, linewise: false) : (SyntaxTarget?)null;
        }

        /// <summary>Linewise when the range owns its lines, as SyntaxTargets decides.</summary>
        private static SyntaxTarget Range(string text, int start, int end)
        {
            int lineStart = LineStart(text, start);
            int lineEnd = LineEnd(text, end > start ? end - 1 : end);

            bool ownsFirst = true;
            for (int i = lineStart; i < start; i++)
                if (!char.IsWhiteSpace(text[i])) { ownsFirst = false; break; }
            bool ownsLast = true;
            for (int i = end; i < lineEnd; i++)
                if (!char.IsWhiteSpace(text[i])) { ownsLast = false; break; }

            return ownsFirst && ownsLast
                ? new SyntaxTarget(lineStart, lineEnd, linewise: true)
                : new SyntaxTarget(start, end, linewise: false);
        }

        private static List<int> Starts(IReadOnlyList<Element> elements, Kind kind) =>
            elements.Where(e => e.Kind == kind).Select(e => e.Start).Distinct().OrderBy(p => p).ToList();

        private static List<int> Ends(IReadOnlyList<Element> elements, Kind kind) =>
            elements.Where(e => e.Kind == kind && e.End > e.Start).Select(e => e.End - 1).Distinct().OrderBy(p => p).ToList();

        private static SyntaxTarget? Next(List<int> positions, int position, int count)
        {
            int seen = 0;
            foreach (var p in positions)
                if (p > position && ++seen == count) return new SyntaxTarget(p, p, linewise: false);
            return null;
        }

        private static SyntaxTarget? Prev(List<int> positions, int position, int count)
        {
            int seen = 0;
            for (int i = positions.Count - 1; i >= 0; i--)
                if (positions[i] < position && ++seen == count) return new SyntaxTarget(positions[i], positions[i], linewise: false);
            return null;
        }

        // ---- lines --------------------------------------------------------

        private static int LineStart(string text, int position)
        {
            int i = position > text.Length ? text.Length : position;
            while (i > 0 && text[i - 1] != '\n') i--;
            return i;
        }

        /// <summary>The end of the line holding position, before its line break.</summary>
        private static int LineEnd(string text, int position)
        {
            int i = position;
            while (i < text.Length && text[i] != '\n' && text[i] != '\r') i++;
            return i;
        }

        private static int LineStartAfter(string text, int position)
        {
            int i = position;
            while (i < text.Length && text[i] != '\n') i++;
            return i < text.Length ? i + 1 : i;
        }

        private static int LineOf(string text, int position)
        {
            int line = 0;
            for (int i = 0; i < position && i < text.Length; i++) if (text[i] == '\n') line++;
            return line;
        }
    }
}
