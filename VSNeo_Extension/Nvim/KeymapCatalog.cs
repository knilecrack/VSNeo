using System;
using System.Collections.Generic;

namespace VSNeo_Extension.Nvim;

/// <summary>
/// The mapping tables the companion pushed after the rc loaded, and the
/// prefix matching the which-key popup and the insert-mapping claim read on
/// the key path.
///
/// Split out of NvimStateHub so the matching rules (tokenization, case folding,
/// &lt;Space&gt; spelling) can be tested without the RPC/redraw hub. Zero I/O:
/// readers see whole immutable tables, replaced in one reference store.
/// </summary>
internal sealed class KeymapCatalog
{
    private Table _normal = Table.Empty;
    private Table _visual = Table.Empty;
    private volatile HashSet<string> _insert = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Is this encoded key ("&lt;Left&gt;", "&lt;C-x&gt;") the lhs of an
    /// insert-mode mapping the user declared? The companion pushes only the
    /// user's own mappings (nvim's defaults are filtered), and only named-key
    /// lhs - so an unconfigured session claims nothing in insert, and a
    /// printable lhs is never here to begin with. Read on the key path: a set
    /// lookup, no I/O.
    /// </summary>
    public bool IsInsertMapped(string keys) => _insert.Contains(keys);

    /// <summary>The mapping table the companion pushed, per mode.</summary>
    public IReadOnlyList<KeymapEntry> KeymapsFor(VimMode mode) => TableFor(mode).Entries;

    /// <summary>
    /// The mappings whose lhs strictly extends <paramref name="prefix"/> (the
    /// keys typed so far, in the same notation the key processor sent them).
    /// Empty when the sequence is complete, unknown, or never started - all
    /// three mean "no hint to show". Allocates only when there is something to
    /// show; the per-keystroke question is <see cref="HasKeymapChildren"/>.
    /// </summary>
    public IReadOnlyList<KeymapEntry> KeymapChildren(VimMode mode, string prefix)
    {
        if (prefix.Length == 0)
        {
            return Array.Empty<KeymapEntry>();
        }

        Table table = TableFor(mode);
        List<KeymapEntry>? result = null;
        for (int m = 0; m < table.Entries.Length; m++)
        {
            if (IsStrictPrefix(prefix, table.Tokens[m]))
            {
                (result ??= new List<KeymapEntry>()).Add(table.Entries[m]);
            }
        }

        return (IReadOnlyList<KeymapEntry>?)result ?? Array.Empty<KeymapEntry>();
    }

    public bool HasKeymapChildren(VimMode mode, string prefix)
    {
        if (prefix.Length == 0)
        {
            return false;
        }

        Table table = TableFor(mode);
        for (int m = 0; m < table.Tokens.Length; m++)
        {
            if (IsStrictPrefix(prefix, table.Tokens[m]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Replaces one mode's table wholesale. Called on the RPC read thread.</summary>
    public void Replace(VimMode mode, IReadOnlyList<KeymapEntry> entries)
    {
        var array = new KeymapEntry[entries.Count];
        var tokens = new string[array.Length][];
        for (int i = 0; i < array.Length; i++)
        {
            array[i] = entries[i];
            tokens[i] = SplitKeyTokens(entries[i].Lhs).ToArray();
        }

        var table = new Table(array, tokens);
        if (mode == VimMode.Normal)
        {
            _normal = table;
        }
        else if (mode == VimMode.Visual)
        {
            _visual = table;
        }
    }

    /// <summary>Replaces the insert-mode mapping set. Called on the RPC read thread.</summary>
    public void SetInsert(IEnumerable<string> keys)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string key in keys)
        {
            if (!string.IsNullOrEmpty(key))
            {
                set.Add(key);
            }
        }

        _insert = set;
    }

    private Table TableFor(VimMode mode)
    {
        if (mode == VimMode.Normal)
        {
            return _normal;
        }

        if (mode == VimMode.Visual)
        {
            return _visual;
        }

        return Table.Empty;
    }

    /// <summary>
    /// Is the token stream of <paramref name="prefix"/> a strict prefix of a
    /// mapping's pre-split tokens? Walks the prefix token by token against the
    /// stored ones, normalizing as it goes ("&lt;C-W&gt;" vs "&lt;C-w&gt;",
    /// "&lt;Space&gt;" vs a plain space), so nothing is allocated per keystroke
    /// and the two sides cannot drift.
    /// </summary>
    private static bool IsStrictPrefix(string prefix, string[] tokens)
    {
        int t = 0, i = 0;
        while (i < prefix.Length)
        {
            if (t >= tokens.Length || !MatchToken(prefix, ref i, tokens[t]))
            {
                return false;
            }

            t++;
        }

        return t < tokens.Length;
    }

    /// <summary>Consumes one token at <paramref name="i"/> and compares it
    /// against a stored normalized token.</summary>
    private static bool MatchToken(string s, ref int i, string token)
    {
        if (s[i] != '<')
        {
            return token.Length == 1 && token[0] == s[i++];
        }

        int close = s.IndexOf('>', i + 1);
        if (close < 0)
        {
            return token.Length == 1 && token[0] == s[i++]; // a trailing '<' is a literal
        }

        int innerStart = i + 1;
        int innerLength = close - innerStart;
        i = close + 1;

        // The folded space token matches only the "<Space>" spelling.
        if (token.Length == 1)
        {
            return token[0] == ' '
                && innerLength == 5
                && (s[innerStart] == 's' || s[innerStart] == 'S')
                && (s[innerStart + 1] == 'p' || s[innerStart + 1] == 'P')
                && (s[innerStart + 2] == 'a' || s[innerStart + 2] == 'A')
                && (s[innerStart + 3] == 'c' || s[innerStart + 3] == 'C')
                && (s[innerStart + 4] == 'e' || s[innerStart + 4] == 'E');
        }

        // Stored tokens are "<lowered inner>"; compare case-insensitively.
        if (token.Length != innerLength + 2 || token[0] != '<' || token[token.Length - 1] != '>')
        {
            return false;
        }

        for (int k = 0; k < innerLength; k++)
        {
            if (token[k + 1] != char.ToLowerInvariant(s[innerStart + k]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Splits nvim key notation into tokens: "&lt;C-w&gt;f" becomes
    /// {"&lt;c-w&gt;", "f"}. Normalized so the two sides of a comparison cannot
    /// drift apart: "&lt;...&gt;" contents are lowercased (nvim reports
    /// "&lt;C-W&gt;", KeyEncoder produces "&lt;C-w&gt;") and "&lt;Space&gt;"
    /// folds to a plain space (KeyEncoder's named key, nvim's expanded leader).
    /// A trailing "&lt;" with no close is a literal character.
    /// </summary>
    public static List<string> SplitKeyTokens(string keys)
    {
        var tokens = new List<string>();
        int i = 0;
        while (i < keys.Length)
        {
            if (keys[i] == '<')
            {
                int close = keys.IndexOf('>', i + 1);
                if (close < 0)
                {
                    tokens.Add(keys[i].ToString());
                    i++;
                    continue;
                }

                var inner = keys.Substring(i + 1, close - i - 1).ToLowerInvariant();
                tokens.Add(inner == "space" ? " " : "<" + inner + ">");
                i = close + 1;
            }
            else
            {
                tokens.Add(keys[i].ToString());
                i++;
            }
        }

        return tokens;
    }

    /// <summary>
    /// A pushed mapping set: the public entries, plus each lhs pre-split into
    /// normalized tokens once, so the per-keystroke prefix match never
    /// tokenizes again. Immutable after construction; the field reference is
    /// swapped whole, so key-path readers never see a half-built table.
    /// </summary>
    private sealed class Table
    {
        public static readonly Table Empty = new Table(
            Array.Empty<KeymapEntry>(), Array.Empty<string[]>());

        public readonly KeymapEntry[] Entries;
        public readonly string[][] Tokens;

        public Table(KeymapEntry[] entries, string[][] tokens)
        {
            Entries = entries;
            Tokens = tokens;
        }
    }
}
