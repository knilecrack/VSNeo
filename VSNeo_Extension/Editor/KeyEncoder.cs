using System.Windows.Input;

namespace VSNeo_Extension.Editor
{
    /// <summary>
    /// Translates a WPF key event into Neovim's key notation ("a", "&lt;Esc&gt;", "&lt;C-w&gt;").
    /// Returns null when the key carries no meaning for nvim, which tells the
    /// key processor to leave it to Visual Studio.
    /// </summary>
    internal static class KeyEncoder
    {
        public static string Encode(KeyEventArgs e)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            var mods = Keyboard.Modifiers;
            bool ctrl = (mods & ModifierKeys.Control) != 0;
            bool alt = (mods & ModifierKeys.Alt) != 0;
            bool shift = (mods & ModifierKeys.Shift) != 0;

            // Ctrl+Alt together is never claimed. On most international layouts it
            // is AltGr, which nvim cannot tell apart from a real chord; and
            // Ctrl+Alt(+Shift)+key is Visual Studio's own binding namespace, so
            // swallowing it here kills those commands outright (nvim almost never
            // maps it). Better to let Visual Studio have it every time.
            if (ctrl && alt) return null;

            var named = Named(key);
            if (named != null) return Wrap(named, ctrl, alt, shift: false);

            if (key >= Key.A && key <= Key.Z)
            {
                var ch = ((char)('a' + (key - Key.A))).ToString();
                if (ctrl || alt) return Wrap(ch, ctrl, alt, shift);
                return null; // plain letters: let TextInput handle them
            }

            // Punctuation only when modified: unmodified it must keep flowing
            // through TextInput, because the character is layout-dependent and
            // WPF has already done that translation by then.
            if (ctrl || alt)
            {
                var punct = Punctuation(key);
                if (punct != null) return Wrap(punct, ctrl, alt, shift);
            }

            return null;
        }

        /// <summary>Printable text arriving via TextCompositionManager. "&lt;" needs escaping.</summary>
        public static string EncodeText(string text) =>
            string.IsNullOrEmpty(text) ? null : text.Replace("<", "<lt>");

        private static string Named(Key key)
        {
            switch (key)
            {
                case Key.Escape: return "Esc";
                case Key.Return: return "CR";
                case Key.Tab: return "Tab";
                case Key.Back: return "BS";
                case Key.Delete: return "Del";
                case Key.Space: return "Space";
                case Key.Up: return "Up";
                case Key.Down: return "Down";
                case Key.Left: return "Left";
                case Key.Right: return "Right";
                case Key.Home: return "Home";
                case Key.End: return "End";
                case Key.PageUp: return "PageUp";
                case Key.PageDown: return "PageDown";
                case Key.Insert: return "Insert";
                default: return null;
            }
        }

        /// <summary>
        /// OEM punctuation, reached only with Ctrl or Alt held. These are US-layout
        /// names; on other layouts the produced token may not match the key cap, the
        /// same trade-off Vim itself makes for Alt-chords. Backslash uses nvim's
        /// Bslash token because a literal '\' inside &lt;&gt; notation misparses.
        /// </summary>
        private static string Punctuation(Key key)
        {
            switch (key)
            {
                case Key.OemOpenBrackets: return "[";
                case Key.OemCloseBrackets: return "]";
                case Key.OemComma: return ",";
                case Key.OemPeriod: return ".";
                case Key.OemSemicolon: return ";";
                case Key.OemQuotes: return "'";
                case Key.OemBackslash: return "Bslash";
                case Key.OemMinus: return "-";
                case Key.OemPlus: return "=";
                default: return null;
            }
        }

        private static string Wrap(string core, bool ctrl, bool alt, bool shift)
        {
            var prefix = string.Empty;
            if (ctrl) prefix += "C-";
            if (alt) prefix += "A-";
            if (shift) prefix += "S-";
            return "<" + prefix + core + ">";
        }
    }
}
