using System;
using System.Collections.Generic;

namespace VSNeo_Extension.Infrastructure
{
    /// <summary>
    /// Screen rows over buffer lines when some lines are folded away.
    ///
    /// nvim and Visual Studio both count a window in screen rows, and a closed
    /// fold is one row however many lines it holds (nvim mirrors Visual
    /// Studio's collapsed regions as closed manual folds, so the two agree).
    /// Arithmetic on buffer lines - "topline + height", "caret - height / 2" -
    /// does not: with a collapsed region on screen it put the bottom of the
    /// window, its centre, and the last topline nvim can show in the wrong
    /// place, and every one of those errors came back as a scroll that yanked
    /// the view (the "jumps a line or two while only the cursor should move"
    /// and flicker-near-the-end reports).
    ///
    /// Works on hidden-line intervals: a collapsed region whose extent runs
    /// from line s to line e keeps s on screen (its header, drawn with the
    /// ellipsis) and hides s+1..e. Plain data, no editor types, so the unit
    /// tests link it directly.
    /// </summary>
    internal sealed class FoldRows
    {
        // Merged, sorted, non-overlapping [first, last] hidden-line intervals.
        private readonly List<KeyValuePair<int, int>> _hidden;

        public static readonly FoldRows None = new FoldRows(new List<KeyValuePair<int, int>>());

        private FoldRows(List<KeyValuePair<int, int>> hidden) => _hidden = hidden;

        /// <summary>
        /// From collapsed regions as (startLine, endLine) pairs, in any order,
        /// nested or overlapping (a collapsed region inside a collapsed parent
        /// is reported too; its lines are hidden once, not twice).
        /// </summary>
        public static FoldRows FromRegions(IEnumerable<KeyValuePair<int, int>> regions)
        {
            var intervals = new List<KeyValuePair<int, int>>();
            foreach (var r in regions)
            {
                int first = r.Key + 1, last = r.Value;
                if (last >= first) intervals.Add(new KeyValuePair<int, int>(first, last));
            }
            if (intervals.Count == 0) return None;

            intervals.Sort((a, b) => a.Key.CompareTo(b.Key));
            var merged = new List<KeyValuePair<int, int>>(intervals.Count);
            var current = intervals[0];
            for (int i = 1; i < intervals.Count; i++)
            {
                var next = intervals[i];
                if (next.Key <= current.Value + 1)
                {
                    if (next.Value > current.Value)
                        current = new KeyValuePair<int, int>(current.Key, next.Value);
                }
                else
                {
                    merged.Add(current);
                    current = next;
                }
            }
            merged.Add(current);
            return new FoldRows(merged);
        }

        /// <summary>True when the line is folded away (not a header, not outside any fold).</summary>
        public bool IsHidden(int line) => IntervalOf(line) >= 0;

        /// <summary>The visible line a hidden line belongs to: its fold's header row.</summary>
        public int VisibleLineOf(int line)
        {
            int i = IntervalOf(line);
            return i < 0 ? line : _hidden[i].Key - 1;
        }

        /// <summary>
        /// Screen rows from <paramref name="from"/> down to <paramref name="to"/>:
        /// 0 when both sit on the same row, negative when <paramref name="to"/>
        /// is above. Hidden lines count nothing.
        /// </summary>
        public int RowsBetween(int from, int to)
        {
            if (to < from) return -RowsBetween(to, from);
            from = VisibleLineOf(from);
            to = VisibleLineOf(to);

            int rows = to - from;
            foreach (var h in _hidden)
            {
                if (h.Key > to) break;
                if (h.Value <= from) continue;
                int lo = Math.Max(h.Key, from + 1), hi = Math.Min(h.Value, to);
                if (hi >= lo) rows -= hi - lo + 1;
            }
            return rows;
        }

        /// <summary>
        /// The line <paramref name="rows"/> screen rows above <paramref name="line"/>,
        /// stopping at line 0. Lands on visible lines only (a fold's header,
        /// never the lines it hides).
        /// </summary>
        public int LineRowsAbove(int line, int rows)
        {
            line = VisibleLineOf(line);
            while (rows > 0 && line > 0)
            {
                line = VisibleLineOf(line - 1);
                rows--;
            }
            return line;
        }

        private int IntervalOf(int line)
        {
            int lo = 0, hi = _hidden.Count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                var h = _hidden[mid];
                if (line < h.Key) hi = mid - 1;
                else if (line > h.Value) lo = mid + 1;
                else return mid;
            }
            return -1;
        }
    }
}
