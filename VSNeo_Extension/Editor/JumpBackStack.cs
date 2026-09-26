using System;
using System.Collections.Generic;

namespace VSNeo_Extension.Editor
{
    /// <summary>
    /// VSNeo's own back/forward stack for jumps it initiates (the goto_cmd
    /// family: gd, gD, gi, gr, [d, ]d and user mappings built on
    /// vsneo.goto_cmd).
    ///
    /// This exists because Visual Studio's navigation history cannot be relied
    /// on for extension-driven navigation: Edit.GoToDefinition is executed
    /// through the active view's IOleCommandTarget (the C++ language service
    /// misbehaves on DTE's global route), and whether a browse-back point with
    /// the exact origin is recorded at all is up to heuristics the caret churn
    /// of the sync machinery further confuses. The visible symptom: gd lands
    /// right, but &lt;C-o&gt; returns to the right file at the wrong spot.
    ///
    /// The package records the caret's spot here before running a jump command
    /// and walks this stack first when View.NavigateBackward/Forward arrives;
    /// only an empty stack falls through to the real Visual Studio command, so
    /// points Visual Studio recorded itself (a pass-through F12, an error-list
    /// click) keep working. A point Visual Studio also recorded for a jump this
    /// stack already returned to may be visited once more when the stacks
    /// interleave - harmless, and preferable to trusting the unreliable one.
    ///
    /// UI thread only; positions are Visual Studio coordinates (0-based line,
    /// UTF-16 column), never nvim's byte columns.
    /// </summary>
    internal static class JumpBackStack
    {
        internal readonly struct Entry
        {
            internal Entry(string path, int line, int column)
            {
                Path = path;
                Line = line;
                Column = column;
            }

            internal string Path { get; }
            internal int Line { get; }
            internal int Column { get; }
        }

        private const int MaxEntries = 200;

        private static readonly List<Entry> _back = new List<Entry>();
        private static readonly List<Entry> _forward = new List<Entry>();

        /// <summary>
        /// Record the jump origin. Clears the forward stack, browser-style: a
        /// new jump makes everything after it unreachable.
        /// </summary>
        internal static void Record(string path, int line, int column)
        {
            _back.Add(new Entry(path, line, column));
            if (_back.Count > MaxEntries) _back.RemoveAt(0);
            _forward.Clear();
        }

        /// <summary>
        /// Take the next target in the given direction. Entries describing the
        /// spot the caret is already on are skipped - a jump command that went
        /// nowhere (gd on a comment) still recorded its origin, and "navigating"
        /// to it would feel like &lt;C-o&gt; did nothing. When a target is taken,
        /// the current spot is pushed onto the opposite stack so the reverse
        /// direction can return to it.
        /// </summary>
        internal static bool TryTake(
            bool backward,
            string currentPath,
            int currentLine,
            int currentColumn,
            out Entry target)
        {
            var from = backward ? _back : _forward;
            var to = backward ? _forward : _back;

            while (from.Count > 0)
            {
                var entry = from[from.Count - 1];
                from.RemoveAt(from.Count - 1);

                if (string.Equals(entry.Path, currentPath, StringComparison.OrdinalIgnoreCase)
                    && entry.Line == currentLine
                    && entry.Column == currentColumn)
                {
                    continue;
                }

                to.Add(new Entry(currentPath, currentLine, currentColumn));
                target = entry;
                return true;
            }

            target = default;
            return false;
        }
    }
}
