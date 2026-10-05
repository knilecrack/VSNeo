// SeekyVS — Visual Studio 2026 port spike for the Seeky VS Code extension.

namespace SeekyVS;

using System;
using System.Text.RegularExpressions;

/// <summary>
/// Decides whether a single source line declares a symbol, and pulls the symbol's kind and name
/// out of it. Line-based and language-heuristic — this is deliberately not a parser.
/// </summary>
/// <remarks>
/// <para>
/// This replaces fff's <c>classify_definitions</c> flag, which the shipped <c>fff_c.dll</c>
/// (v0.10.1) reports as <c>false</c> for every match — including obvious declarations like
/// <c>public Task StartAsync(...)</c>. The <c>is_definition</c> bit is still read from the
/// native side but is not trusted; see <see cref="SymbolIndex"/> for how the results are used.
/// </para>
/// <para>
/// The C-family rule is gated on a leading declaration modifier (<c>public</c>, <c>static</c>, …).
/// That is what keeps ordinary statements — <c>return new Foo(x);</c> — from being classified as
/// declarations, at the cost of missing modifier-less members (C++ methods, implicitly-private C#
/// members, local functions). Over-rejecting is the right failure direction here: a picker full of
/// call sites is worse than one missing a few declarations.
/// </para>
/// </remarks>
internal static partial class SymbolClassifier
{
    /// <summary>A symbol declared on a source line. Ranges are char indices into that line.</summary>
    internal readonly record struct Symbol(string Kind, string Name, int NameStart, int NameEnd);

    // Identifiers that can precede a '(' or '{' without the line being a declaration. Without
    // this, "public ... if (x) {" shapes and control flow inside modifier-led lines slip through.
    private static readonly string[] NonSymbolNames =
    [
        "if", "for", "foreach", "while", "switch", "catch", "lock", "using", "fixed", "return",
        "new", "throw", "await", "yield", "else", "do", "try", "when", "in", "is", "as",
    ];

    /// <summary>
    /// Whether <paramref name="line"/> declares a symbol — the predicate behind the grep
    /// <c>def</c> badge and the Ctrl+D filter.
    /// </summary>
    internal static bool IsDefinition(string path, string line) => TryClassify(path, line, out _);

    /// <summary>
    /// Classifies <paramref name="line"/> from <paramref name="path"/> (whose extension picks the
    /// language rules). Returns false for comments, blank lines and anything that does not look
    /// like a declaration.
    /// </summary>
    internal static bool TryClassify(string path, string line, out Symbol symbol)
    {
        symbol = default;
        if (string.IsNullOrWhiteSpace(line) || line.Length > 400)
        {
            return false;
        }

        int indent = 0;
        while (indent < line.Length && char.IsWhiteSpace(line[indent]))
        {
            indent++;
        }

        // Comment lines are the single biggest source of false positives: XML doc comments quote
        // declarations verbatim ("/// <see cref="public void M()"/>").
        ReadOnlySpan<char> trimmed = line.AsSpan(indent);
        if (trimmed.StartsWith("//") || trimmed.StartsWith("/*") || trimmed.StartsWith("*")
            || trimmed.StartsWith("'") || trimmed.StartsWith("--"))
        {
            return false;
        }

        string extension = GetExtension(path);
        return extension switch
        {
            ".py" => TryPython(line, out symbol),
            ".rs" => TryRust(line, out symbol),
            ".go" => TryGo(line, out symbol),
            ".vb" => TryVisualBasic(line, out symbol),
            ".ts" or ".tsx" or ".js" or ".jsx" or ".mjs" or ".cjs" => TryScript(line, out symbol),
            ".cs" or ".java" or ".cpp" or ".cc" or ".cxx" or ".c" or ".h" or ".hpp" or ".hxx"
                => TryCFamily(line, out symbol),
            _ => false,
        };
    }

    // ------------------------------------------------------------------ C# / Java / C++

    private static bool TryCFamily(string line, out Symbol symbol)
    {
        symbol = default;

        // Type declarations first: "public sealed record struct Foo" matches both this rule and
        // the member rule below, and the type kind is the more useful answer.
        Match type = CFamilyTypeRegex().Match(line);
        if (type.Success)
        {
            Group name = type.Groups["name"];
            symbol = new Symbol(
                NormalizeKind(type.Groups["kind"].Value),
                name.Value,
                name.Index,
                name.Index + name.Length);
            return true;
        }

        // Members: require at least one leading modifier, then read the declared name off the
        // first structural delimiter that follows it.
        Match member = CFamilyMemberRegex().Match(line);
        if (!member.Success)
        {
            return false;
        }

        int start = member.Index + member.Length;
        return TryReadNameBeforeDelimiter(line, start, out symbol);
    }

    /// <summary>
    /// Reads the declared name backwards from the first of <c>( { =&gt; = ;</c> at or after
    /// <paramref name="start"/>, and derives the kind from which delimiter was found:
    /// <c>(</c> method, <c>{</c> or <c>=&gt;</c> property, <c>=</c> or <c>;</c> field.
    /// </summary>
    private static bool TryReadNameBeforeDelimiter(string line, int start, out Symbol symbol)
    {
        symbol = default;

        int delimiter = -1;
        string kind = string.Empty;
        for (int i = start; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '(')
            {
                delimiter = i;
                kind = "method";
                break;
            }

            if (c == '{')
            {
                delimiter = i;
                kind = "property";
                break;
            }

            if (c == '=' && i + 1 < line.Length && line[i + 1] == '>')
            {
                delimiter = i;
                kind = "property";
                break;
            }

            if (c == '=' || c == ';')
            {
                delimiter = i;
                kind = "field";
                break;
            }
        }

        if (delimiter < 0)
        {
            return false;
        }

        int end = delimiter;
        while (end > start && char.IsWhiteSpace(line[end - 1]))
        {
            end--;
        }

        // Generic parameter list on a method name: "Get<T>(" — step back over it to reach "Get".
        if (end > start && line[end - 1] == '>')
        {
            int depth = 0;
            int scan = end - 1;
            while (scan >= start)
            {
                if (line[scan] == '>')
                {
                    depth++;
                }
                else if (line[scan] == '<')
                {
                    depth--;
                    if (depth == 0)
                    {
                        break;
                    }
                }

                scan--;
            }

            if (scan < start)
            {
                return false;
            }

            end = scan;
            while (end > start && char.IsWhiteSpace(line[end - 1]))
            {
                end--;
            }
        }

        int nameStart = end;
        while (nameStart > start && (char.IsLetterOrDigit(line[nameStart - 1]) || line[nameStart - 1] == '_'))
        {
            nameStart--;
        }

        if (nameStart == end || char.IsDigit(line[nameStart]))
        {
            return false;
        }

        string name = line[nameStart..end];
        if (Array.IndexOf(NonSymbolNames, name) >= 0)
        {
            return false;
        }

        symbol = new Symbol(kind, name, nameStart, end);
        return true;
    }

    // ------------------------------------------------------------------ other languages

    private static bool TryPython(string line, out Symbol symbol) =>
        TryKeywordRule(PythonRegex().Match(line), out symbol);

    private static bool TryRust(string line, out Symbol symbol) =>
        TryKeywordRule(RustRegex().Match(line), out symbol);

    private static bool TryGo(string line, out Symbol symbol) =>
        TryKeywordRule(GoRegex().Match(line), out symbol);

    private static bool TryScript(string line, out Symbol symbol)
    {
        if (TryKeywordRule(ScriptRegex().Match(line), out symbol))
        {
            return true;
        }

        // TS/JS class members and object properties carry no keyword: "foo(args) {" / "foo: 1".
        Match member = ScriptMemberRegex().Match(line);
        if (!member.Success)
        {
            return false;
        }

        Group name = member.Groups["name"];
        if (Array.IndexOf(NonSymbolNames, name.Value) >= 0)
        {
            return false;
        }

        symbol = new Symbol(
            member.Groups["paren"].Success ? "method" : "property",
            name.Value,
            name.Index,
            name.Index + name.Length);
        return true;
    }

    private static bool TryVisualBasic(string line, out Symbol symbol) =>
        TryKeywordRule(VisualBasicRegex().Match(line), out symbol);

    private static bool TryKeywordRule(Match match, out Symbol symbol)
    {
        symbol = default;
        if (!match.Success)
        {
            return false;
        }

        Group name = match.Groups["name"];
        symbol = new Symbol(
            NormalizeKind(match.Groups["kind"].Value),
            name.Value,
            name.Index,
            name.Index + name.Length);
        return true;
    }

    /// <summary>Collapses language keywords onto the badge vocabulary the picker renders.</summary>
    private static string NormalizeKind(string keyword)
    {
        string normalized = WhitespaceRegex().Replace(keyword.Trim(), " ").ToLowerInvariant();
        return normalized switch
        {
            "fn" or "func" or "def" or "sub" or "function" => "method",
            "record class" or "record struct" => "record",
            "module" or "mod" or "namespace" or "package" => "namespace",
            "structure" => "struct",
            "trait" => "interface",
            "type" or "typedef" => "type",
            "const" or "let" or "var" or "dim" => "field",
            _ => normalized,
        };
    }

    private static string GetExtension(string path)
    {
        int dot = path.LastIndexOf('.');
        if (dot < 0 || dot == path.Length - 1)
        {
            return string.Empty;
        }

        int separator = path.LastIndexOfAny(['/', '\\']);
        return dot < separator ? string.Empty : path[dot..].ToLowerInvariant();
    }

    // ------------------------------------------------------------------ patterns

    [GeneratedRegex(
        @"^\s*(?:\[[^\]]*\]\s*)*(?:(?:public|private|protected|internal|static|sealed|abstract|partial|readonly|ref|file|new|unsafe)\s+)*(?<kind>record\s+struct|record\s+class|class|interface|struct|record|enum|delegate|namespace)\s+(?<name>[A-Za-z_]\w*)",
        RegexOptions.ExplicitCapture)]
    private static partial Regex CFamilyTypeRegex();

    [GeneratedRegex(
        @"^\s*(?:\[[^\]]*\]\s*)*(?:(?:public|private|protected|internal|static|async|virtual|override|abstract|sealed|partial|extern|unsafe|new|readonly|const|required|volatile|event|file|explicit|implicit|operator)\s+)+",
        RegexOptions.ExplicitCapture)]
    private static partial Regex CFamilyMemberRegex();

    [GeneratedRegex(
        @"^\s*(?:async\s+)?(?<kind>def|class)\s+(?<name>[A-Za-z_]\w*)",
        RegexOptions.ExplicitCapture)]
    private static partial Regex PythonRegex();

    [GeneratedRegex(
        @"^\s*(?:pub(?:\([^)]*\))?\s+)?(?:default\s+|async\s+|unsafe\s+|const\s+|extern\s+(?:""[^""]*""\s+)?)*(?<kind>fn|struct|enum|trait|impl|type|mod|union|macro_rules!)\s+(?<name>[A-Za-z_]\w*)",
        RegexOptions.ExplicitCapture)]
    private static partial Regex RustRegex();

    [GeneratedRegex(
        @"^\s*(?<kind>func|type)\s+(?:\([^)]*\)\s*)?(?<name>[A-Za-z_]\w*)",
        RegexOptions.ExplicitCapture)]
    private static partial Regex GoRegex();

    [GeneratedRegex(
        @"^\s*(?:export\s+)?(?:default\s+)?(?:declare\s+)?(?:abstract\s+)?(?:async\s+)?(?<kind>function\s*\*?|class|interface|type|enum|const|let|var)\s+(?<name>[A-Za-z_$][\w$]*)",
        RegexOptions.ExplicitCapture)]
    private static partial Regex ScriptRegex();

    [GeneratedRegex(
        @"^\s{2,}(?:(?:public|private|protected|static|readonly|async|get|set|override)\s+)*(?<name>[A-Za-z_$][\w$]*)\s*(?:(?<paren>\()|:\s*\S)",
        RegexOptions.ExplicitCapture)]
    private static partial Regex ScriptMemberRegex();

    [GeneratedRegex(
        @"^\s*(?:(?:Public|Private|Protected|Friend|Shared|Overrides|Overridable|MustOverride|NotOverridable|Partial|ReadOnly|Default)\s+)*(?<kind>Sub|Function|Property|Class|Module|Structure|Enum|Interface|Delegate)\s+(?<name>[A-Za-z_]\w*)",
        RegexOptions.ExplicitCapture | RegexOptions.IgnoreCase)]
    private static partial Regex VisualBasicRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
