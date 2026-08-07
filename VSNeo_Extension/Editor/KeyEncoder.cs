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

            var named = Named(key);
            if (named != null) return Wrap(named, ctrl, alt, shift: false);

            if (key >= Key.A && key <= Key.Z)
            {
                var ch = ((char)('a' + (key - Key.A))).ToString();
                if (ctrl || alt) return Wrap(ch, ctrl, alt, shift);
                return null; // plain letters: let TextInput handle them
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
