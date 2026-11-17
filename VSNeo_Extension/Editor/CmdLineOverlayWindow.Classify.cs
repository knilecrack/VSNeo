using System;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace VSNeo_Extension.Editor;

// What kind of command the line holds (ex, search, filter, shell), for the accent color.
internal static partial class CmdLineOverlayWindow
{
    /// <summary>What is being typed, and how it should look.</summary>
    private readonly struct CmdLineKind
    {
        public readonly string Title;
        public readonly Color Accent;
        public readonly int CommandStart;
        public readonly int CommandLength;

        public CmdLineKind(string title, Color accent, int commandStart = -1, int commandLength = 0)
        {
            Title = title;
            Accent = accent;
            CommandStart = commandStart;
            CommandLength = commandLength;
        }
    }

    // tokyonight's accents: distinct at a glance, readable on dark themes
    // (darkened for light ones in Render).
    private static readonly Color Blue = Color.FromRgb(0x7A, 0xA2, 0xF7);
    private static readonly Color Amber = Color.FromRgb(0xE0, 0xAF, 0x68);
    private static readonly Color Cyan = Color.FromRgb(0x7D, 0xCF, 0xFF);
    private static readonly Color Red = Color.FromRgb(0xF7, 0x76, 0x8E);
    private static readonly Color Green = Color.FromRgb(0x9E, 0xCE, 0x6A);
    private static readonly Color Purple = Color.FromRgb(0xBB, 0x9A, 0xF7);
    private static readonly Color Orange = Color.FromRgb(0xFF, 0x9E, 0x64);

    // An optional range (%, '<,'>, 1,5, .,$+3), then the command name - or
    // a bare ! (a shell filter).
    private static readonly Regex CommandName = new Regex(
        @"^\s*(?:%|'<,'>|[\d.$+\-,;]*)\s*([A-Za-z]+|!)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The prompt says which mechanism is open (":", "/", "?", "=" or an
    /// input() prompt); for ":" the command name narrows it further.
    /// </summary>
    private static CmdLineKind Classify(string prefix, string content)
    {
        switch (prefix)
        {
            case "/":
                return new CmdLineKind("Search", Amber);
            case "?":
                return new CmdLineKind("Search \u2191", Amber);
            case "=":
                return new CmdLineKind("Expression", Orange);
            case ":":
                break;
            default:
                return new CmdLineKind("Input", Green);
        }

        if (content.TrimStart().StartsWith("=", StringComparison.Ordinal))
        {
            return new CmdLineKind("Lua", Cyan);
        }

        var m = CommandName.Match(content);
        if (!m.Success)
        {
            return new CmdLineKind("Cmdline", Blue);
        }

        var group = m.Groups[1];
        string name = group.Value;
        int start = group.Index, length = group.Length;

        if (name == "!")
        {
            return new CmdLineKind("Shell", Red, start, length);
        }

        if (name == "lua" || name == "luafile" || name == "luado")
        {
            return new CmdLineKind("Lua", Cyan, start, length);
        }

        if (IsAbbreviation(name, "help", 1))
        {
            return new CmdLineKind("Help", Green, start, length);
        }

        // :s[ubstitute] and :g[lobal]/:v[global] take a delimiter right
        // after the name: :s/a/b/, :g/pat/d. :set and :sort do not match -
        // they are not abbreviations of either.
        int after = start + length;
        bool delimited = after < content.Length && !char.IsLetterOrDigit(content[after])
                         && !char.IsWhiteSpace(content[after]) && content[after] != '"' && content[after] != '|';
        if (delimited && IsAbbreviation(name, "substitute", 1))
        {
            return new CmdLineKind("Substitute", Purple, start, length);
        }

        if (delimited && (IsAbbreviation(name, "global", 1) || IsAbbreviation(name, "vglobal", 1)))
        {
            return new CmdLineKind("Global", Purple, start, length);
        }

        return new CmdLineKind("Cmdline", Blue, start, length);
    }

    /// <summary>Vim's :s[ubstitute] rule: at least the required prefix, and a prefix of the full name.</summary>
    private static bool IsAbbreviation(string name, string full, int required) =>
        name.Length >= required && name.Length <= full.Length
        && string.CompareOrdinal(full, 0, name, 0, name.Length) == 0;
}
