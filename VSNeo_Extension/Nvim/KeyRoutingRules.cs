using System;
using System.Collections.Generic;

namespace VSNeo_Extension.Nvim
{
    internal enum KeyOwner
    {
        Unspecified,
        Ide,
        Vim,
    }

    /// <summary>Immutable-per-lookup, copy-on-write :Sethandler rules.</summary>
    internal sealed class KeyRoutingRules
    {
        private const int ModeCount = 8;
        private static readonly KeyOwner[] EmptyOwners = CreateOwners();
        private readonly object _sync = new object();
        private volatile Snapshot _snapshot = new Snapshot(
            CreateOwners(), new Dictionary<string, KeyOwner[]>(StringComparer.OrdinalIgnoreCase));

        private sealed class Snapshot
        {
            public readonly KeyOwner[] Defaults;
            public readonly Dictionary<string, KeyOwner[]> Keys;

            public Snapshot(KeyOwner[] defaults, Dictionary<string, KeyOwner[]> keys)
            {
                Defaults = defaults;
                Keys = keys;
            }
        }

        public KeyOwner OwnerFor(string? keys, VimMode mode)
        {
            int index = (int)mode;
            if (index < 0 || index >= ModeCount) return KeyOwner.Unspecified;
            var snapshot = _snapshot;
            if (keys != null && snapshot.Keys.TryGetValue(keys, out var owners)
                && owners[index] != KeyOwner.Unspecified)
                return owners[index];
            return IsShortcut(keys) ? snapshot.Defaults[index] : KeyOwner.Unspecified;
        }

        /// <summary>
        /// What a keyless default covers: shortcuts - Ctrl and Alt chords and the
        /// function keys, IdeaVim's sethandler scope - never typed text, Escape
        /// or the editing keys (&lt;BS&gt;, &lt;CR&gt;, &lt;Tab&gt;, arrows). A
        /// default reaching those turned one rc line (n:ide) into hjkl typed
        /// into the file and normal-mode &lt;BS&gt; run as Visual Studio's
        /// Backspace. An explicit rule for one of them (&lt;CR&gt; n:vim) still
        /// applies. KeyEncoder writes Ctrl and Alt ahead of Shift.
        /// </summary>
        private static bool IsShortcut(string? keys) =>
            keys != null && (keys.StartsWith("<C-", StringComparison.OrdinalIgnoreCase)
                || keys.StartsWith("<A-", StringComparison.OrdinalIgnoreCase)
                || (keys.Length >= 4 && (keys[1] == 'F' || keys[1] == 'f') && char.IsDigit(keys[2])));

        public bool Apply(string definition, out string error)
        {
            error = string.Empty;
            var parts = definition.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                error = "expected <key> or mode defaults followed by mode:ide|vim";
                return false;
            }

            string? keys = null;
            int firstAssignment = 0;
            if (parts[0].StartsWith("<", StringComparison.Ordinal))
            {
                keys = parts[0];
                if (keys.Length < 3 || keys[keys.Length - 1] != '>'
                    || keys.IndexOf('>', 1) != keys.Length - 1)
                {
                    error = "expected one named key such as <C-p> or <Esc>";
                    return false;
                }
                firstAssignment = 1;
            }
            if (parts.Length == firstAssignment)
            {
                error = "expected at least one mode:ide or mode:vim assignment";
                return false;
            }

            var changes = new List<KeyValuePair<int, KeyOwner>>();
            for (int i = firstAssignment; i < parts.Length; i++)
            {
                var assignment = parts[i].Split(':');
                if (assignment.Length != 2 || !TryOwner(assignment[1], out var owner))
                {
                    error = "invalid assignment '" + parts[i] + "'; expected mode:ide or mode:vim";
                    return false;
                }
                if (!TryModes(assignment[0], out var modes))
                {
                    error = "invalid mode list '" + assignment[0] + "'";
                    return false;
                }
                for (int mode = 0; mode < ModeCount; mode++)
                    if ((modes & (1 << mode)) != 0)
                        changes.Add(new KeyValuePair<int, KeyOwner>(mode, owner));
            }

            lock (_sync)
            {
                var current = _snapshot;
                var defaults = (KeyOwner[])current.Defaults.Clone();
                var perKey = new Dictionary<string, KeyOwner[]>(current.Keys, StringComparer.OrdinalIgnoreCase);
                KeyOwner[] owners;
                if (keys == null)
                {
                    owners = defaults;
                }
                else if (!perKey.TryGetValue(keys, out owners!))
                {
                    owners = (KeyOwner[])EmptyOwners.Clone();
                    perKey.Add(keys, owners);
                }
                else
                {
                    owners = (KeyOwner[])owners.Clone();
                    perKey[keys] = owners;
                }
                foreach (var change in changes) owners[change.Key] = change.Value;
                _snapshot = new Snapshot(defaults, perKey);
            }
            return true;
        }

        private static KeyOwner[] CreateOwners() => new KeyOwner[ModeCount];

        private static bool TryOwner(string text, out KeyOwner owner)
        {
            if (string.Equals(text, "ide", StringComparison.OrdinalIgnoreCase))
            {
                owner = KeyOwner.Ide;
                return true;
            }
            if (string.Equals(text, "vim", StringComparison.OrdinalIgnoreCase))
            {
                owner = KeyOwner.Vim;
                return true;
            }
            owner = KeyOwner.Unspecified;
            return false;
        }

        private static bool TryModes(string text, out int modes)
        {
            modes = 0;
            if (text.Length == 0) return false;
            if (text.Equals("a", StringComparison.OrdinalIgnoreCase))
            {
                modes = (1 << ModeCount) - 1;
                return true;
            }

            // IdeaVim's '-' separates a list, not a range: n-v is Normal and
            // Visual, never the Insert that sits between them in ModeIndex.
            foreach (var letter in text.Split('-'))
            {
                if (letter.Length != 1) return false;
                int index = ModeIndex(letter[0]);
                if (index < 0 || index == 6) return false;   // 'a' only stands alone
                modes |= ModeMask(index);
            }
            return modes != 0;
        }

        private static int ModeIndex(char mode)
        {
            switch (char.ToLowerInvariant(mode))
            {
                case 'n': return 0;
                case 'i': return 1;
                case 'x': return 2;
                case 's': return 3;
                case 'v': return 4;
                case 'c': return 5;
                case 'a': return 6;
                case 'r': return 7;
                case 'o': return 8;
                default: return -1;
            }
        }

        private static int ModeMask(int index)
        {
            switch (index)
            {
                case 0: return 1 << (int)VimMode.Normal;
                case 1: return 1 << (int)VimMode.Insert;
                case 2:
                case 3:
                case 4: return 1 << (int)VimMode.Visual;
                case 5: return 1 << (int)VimMode.CmdLine;
                case 7: return 1 << (int)VimMode.Replace;
                case 8: return 1 << (int)VimMode.OperatorPending;
                default: return 0;
            }
        }
    }
}
