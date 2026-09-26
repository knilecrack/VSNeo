using System;
using VSNeo_Extension.Nvim;

namespace VSNeo_Extension.Editor
{
    /// <summary>
    /// A caret shape for <see cref="CaretAdornment"/> to draw: a full block, a
    /// horizontal bar (Vim's horNN) or a vertical bar (verNN). Percent is the
    /// bar's thickness as a percentage of the cell; it is 100 for a block.
    /// </summary>
    internal readonly struct CaretShape : IEquatable<CaretShape>
    {
        internal enum ShapeKind { Block, Horizontal, Vertical }

        internal ShapeKind Kind { get; }
        internal int Percent { get; }
        internal bool Blinks { get; }

        private CaretShape(ShapeKind kind, int percent, bool blinks)
        {
            Kind = kind;
            Percent = Math.Min(100, Math.Max(1, percent));
            Blinks = blinks;
        }

        internal static CaretShape FullBlock(bool blinks = true) =>
            new CaretShape(ShapeKind.Block, 100, blinks);

        internal static CaretShape Horizontal(int percent, bool blinks = true) =>
            new CaretShape(ShapeKind.Horizontal, percent, blinks);

        internal static CaretShape Vertical(int percent, bool blinks = true) =>
            new CaretShape(ShapeKind.Vertical, percent, blinks);

        public bool Equals(CaretShape other) =>
            Kind == other.Kind && Percent == other.Percent && Blinks == other.Blinks;

        public override bool Equals(object obj) => obj is CaretShape other && Equals(other);

        public override int GetHashCode() => (Kind, Percent, Blinks).GetHashCode();

        public static bool operator ==(CaretShape a, CaretShape b) => a.Equals(b);

        public static bool operator !=(CaretShape a, CaretShape b) => !a.Equals(b);
    }

    /// <summary>
    /// Reads nvim's 'guicursor' option the way a GUI would: mode groups like
    /// "n-v-c:block" / "r:hor20" / "i:ver25", resolved per mode, first
    /// matching group wins, "a" matching every mode. What Vim's option means
    /// here is slightly adapted:
    ///
    /// - An EMPTY push keeps VSNeo's VsVim-style default table (block in
    ///   normal/terminal, hor50 in operator-pending, hor25 in replace, the
    ///   native thin caret in insert) rather than nvim's own defaults. The
    ///   companion sends empty unless the user actually customized the
    ///   option - nvim ships a non-empty built-in default, and honoring it
    ///   would change the out-of-box look.
    /// - A mode a non-empty option does not name falls back to that same
    ///   default table, not to nvim's.
    /// - Visual and the command line resolve to nothing regardless: visual's
    ///   live end belongs to VisualBlockCaretAdornment, and the command line
    ///   is the overlay's, so a shape there would fight its owner.
    /// - blink modifiers are honored only as on/off: blinkon0 freezes the
    ///   block (Vim's way to say "no blink"); any other blinkon period blinks
    ///   at the system caret rate. blinkwait and blinkoff periods are ignored.
    /// </summary>
    internal static class GuiCursor
    {
        internal static CaretShape? ShapeFor(VimMode mode, string guicursor)
        {
            if (mode == VimMode.Visual || mode == VimMode.CmdLine) return null;

            if (!string.IsNullOrWhiteSpace(guicursor)
                && TryResolve(ModeKey(mode), guicursor, out var shape))
            {
                return shape;
            }

            return DefaultShape(mode);
        }

        private static CaretShape? DefaultShape(VimMode mode) => mode switch
        {
            VimMode.Normal or VimMode.Terminal => CaretShape.FullBlock(),
            VimMode.OperatorPending => CaretShape.Horizontal(50),
            VimMode.Replace => CaretShape.Horizontal(25),
            _ => null,   // insert keeps Visual Studio's thin caret
        };

        private static string ModeKey(VimMode mode) => mode switch
        {
            VimMode.Normal => "n",
            VimMode.OperatorPending => "o",
            VimMode.Insert => "i",
            VimMode.Replace => "r",
            VimMode.Terminal => "t",
            _ => string.Empty,
        };

        private static bool TryResolve(string modeKey, string guicursor, out CaretShape shape)
        {
            shape = default;
            if (modeKey.Length == 0) return false;

            string? allModesSpec = null;
            foreach (var group in guicursor.Split(','))
            {
                int colon = group.IndexOf(':');
                if (colon <= 0 || colon == group.Length - 1) continue;

                var spec = group.Substring(colon + 1);
                bool exact = false;
                foreach (var key in group.Substring(0, colon).Split('-'))
                {
                    if (key == modeKey) exact = true;
                    else if (key == "a" && allModesSpec == null) allModesSpec = spec;
                }

                if (exact && TryParseShape(spec, out shape)) return true;
            }

            return allModesSpec != null && TryParseShape(allModesSpec, out shape);
        }

        private static bool TryParseShape(string spec, out CaretShape shape)
        {
            shape = default;
            var tokens = spec.Split('-');
            var kind = tokens[0];

            bool blinks = true;
            for (int i = 1; i < tokens.Length; i++)
            {
                var attr = tokens[i];
                if (attr == "blinkon0") blinks = false;
                else if (attr.StartsWith("blinkon", StringComparison.Ordinal)) blinks = true;
            }

            if (kind == "block")
            {
                shape = CaretShape.FullBlock(blinks);
                return true;
            }
            if (kind.StartsWith("hor", StringComparison.Ordinal)
                && TryPercent(kind.Substring(3), 20, out int horizontal))
            {
                shape = CaretShape.Horizontal(horizontal, blinks);
                return true;
            }
            if (kind.StartsWith("ver", StringComparison.Ordinal)
                && TryPercent(kind.Substring(3), 25, out int vertical))
            {
                shape = CaretShape.Vertical(vertical, blinks);
                return true;
            }
            return false;
        }

        private static bool TryPercent(string text, int fallback, out int percent)
        {
            percent = fallback;
            return text.Length == 0
                || (int.TryParse(text, out percent) && percent > 0);
        }
    }
}
