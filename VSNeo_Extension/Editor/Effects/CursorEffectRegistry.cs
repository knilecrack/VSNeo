using System;
using System.Collections.Generic;

namespace VSNeo_Extension.Editor.Effects
{
    /// <summary>
    /// Effect names to factories. The names are what users put in
    /// vim.g.vsneo_cursor_vfx_mode; each view creates its own instances, so
    /// effects may keep per-view state freely.
    ///
    /// To add an effect, write the class and add one line to
    /// <see cref="RegisterBuiltIns"/>. <see cref="Register"/> is also open for
    /// anything that wants to add effects at runtime (it must run on the UI
    /// thread, before the effect is used).
    /// </summary>
    internal static class CursorEffectRegistry
    {
        private static readonly Dictionary<string, Func<ICursorEffect>> Factories =
            new Dictionary<string, Func<ICursorEffect>>(StringComparer.OrdinalIgnoreCase);

        static CursorEffectRegistry()
        {
            RegisterBuiltIns();
        }

        private static void RegisterBuiltIns()
        {
            // Neovide's cursor VFX.
            Register("railgun", () => new RailgunEffect());
            Register("torpedo", () => new TorpedoEffect());
            Register("pixiedust", () => new PixieDustEffect());
            Register("sonicboom", () => new SonicBoomEffect());
            Register("ripple", () => new RippleEffect());
            Register("wireframe", () => new WireframeEffect());

            // Cyberpunk set.
            Register("glitch", () => new GlitchEffect());
            Register("matrix", () => new MatrixEffect());
            Register("circuit", () => new CircuitEffect());
            Register("scanline", () => new ScanlineEffect());
            Register("sparks", () => new SparksEffect());
            Register("flicker", () => new FlickerEffect());
        }

        /// <summary>Adds or replaces the effect called <paramref name="name"/>.</summary>
        public static void Register(string name, Func<ICursorEffect> factory)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("An effect needs a name.", nameof(name));
            Factories[name.Trim()] = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        public static bool IsKnown(string name) => Factories.ContainsKey(name);

        public static ICursorEffect? Create(string name) =>
            Factories.TryGetValue(name, out var factory) ? factory() : null;

        /// <summary>
        /// The known effect names in a vsneo_cursor_vfx_mode string (comma,
        /// space or semicolon separated; the companion joins a Lua list with
        /// commas), in order, without duplicates. Unknown names are ignored,
        /// so a typo turns one effect off rather than all of them.
        /// </summary>
        public static List<string> ParseNames(string? text)
        {
            var names = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return names;

            foreach (var raw in text!.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var name = raw.Trim().ToLowerInvariant();
                if (IsKnown(name) && !names.Contains(name)) names.Add(name);
            }
            return names;
        }
    }
}
