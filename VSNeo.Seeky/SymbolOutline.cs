// SeekyVS — Visual Studio 2026 port spike for the Seeky VS Code extension.

namespace VSNeo.Seeky;

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

/// <summary>
/// The document outline behind the "Seeky: Document Outline" picker: every declaration in a
/// file (<see cref="ClassifyAll"/>), plus the symbol chain (namespace › type › member)
/// enclosing a caret line (<see cref="ChainAt"/>). Line-based and indentation-driven — like
/// <see cref="SymbolClassifier"/>, deliberately not a parser.
/// </summary>
/// <remarks>
/// <para>
/// Containment is inferred from indentation: a declaration is taken to contain every
/// more-indented declaration below it, up to the next declaration at its own indent or less.
/// A <c>namespace</c> only ends at another namespace, so file-scoped namespaces (which sit at
/// the same indent as the types that follow them) stay on the chain.
/// </para>
/// <para>
/// Known imprecision, accepted for this heuristic: without brace tracking a caret in the
/// whitespace below a member still attributes to that member, and a multi-line signature is
/// only seen on its first line.
/// </para>
/// </remarks>
internal static class SymbolOutline
{
    /// <summary>
    /// A declaration in the file: what it is, what it's called, the 1-based line it was
    /// declared on, and the visual indent of that line (columns, tab = 4).
    /// </summary>
    internal readonly record struct Entry(string Kind, string Name, int Line, int Indent);

    // First tokens that make "Token Name(" a statement, not a declaration ("return Foo(",
    // "await Bar("). Only consulted by the relaxed C-family rule — the keyword-led rules in
    // SymbolClassifier already reject control flow.
    private static readonly string[] StatementLeadTokens =
    [
        "if", "for", "foreach", "while", "switch", "catch", "lock", "using", "fixed", "return",
        "new", "throw", "await", "yield", "else", "do", "sizeof", "typeof", "nameof", "checked",
        "unchecked",
    ];

    /// <summary>Every declaration in <paramref name="lines"/>, in line order.</summary>
    internal static List<Entry> ClassifyAll(string path, IReadOnlyList<string> lines)
    {
        var entries = new List<Entry>();
        bool relaxedMembers = IsCFamily(path);

        for (int i = 0; i < lines.Count; i++)
        {
            string text = lines[i];
            string? kind = null;
            string? name = null;

            if (SymbolClassifier.TryClassify(path, text, out SymbolClassifier.Symbol symbol))
            {
                kind = symbol.Kind;
                name = symbol.Name;
            }
            else if (relaxedMembers)
            {
                // SymbolClassifier's C-family member rule requires a leading modifier, which
                // implicitly-private C# members don't have — fine for a workspace picker,
                // hole-y for an in-document outline. "Type Name(" has no modifier to lean on,
                // so the statement shapes ("return Foo(") are excluded by the lead token.
                Match relaxed = RelaxedMemberRegex().Match(text);
                if (relaxed.Success
                    && Array.IndexOf(StatementLeadTokens, relaxed.Groups["type"].Value) < 0)
                {
                    kind = "method";
                    name = relaxed.Groups["name"].Value;
                }
            }

            if (kind is null || name is null)
            {
                continue;
            }

            entries.Add(new Entry(kind, name, i + 1, IndentOf(text)));
        }

        return entries;
    }

    /// <summary>
    /// The chain of declarations enclosing <paramref name="caretLine"/> (1-based), ordered
    /// root-first, out of a <see cref="ClassifyAll"/> outline. Empty when the caret sits above
    /// or outside every declaration.
    /// </summary>
    internal static List<Entry> ChainAt(IReadOnlyList<Entry> outline, int caretLine)
    {
        var chain = new List<Entry>();
        foreach (Entry entry in outline)
        {
            if (entry.Line > caretLine)
            {
                break; // the outline is in line order
            }

            while (chain.Count > 0)
            {
                // net472: System.Index is not in the framework, so ^1 is spelled out.
                Entry top = chain[chain.Count - 1];
                if (top.Indent < entry.Indent)
                {
                    break;
                }

                // A namespace only ends at another namespace of its rank: file-scoped
                // namespaces share their indent with the types that follow them.
                if (top.Kind == "namespace" && entry.Kind != "namespace")
                {
                    break;
                }

                chain.RemoveAt(chain.Count - 1);
            }

            chain.Add(entry);
        }

        return chain;
    }

    /// <summary>Leading whitespace as a visual column; a tab advances to the next multiple of 4.</summary>
    private static int IndentOf(string line)
    {
        int column = 0;
        foreach (char c in line)
        {
            if (c == ' ')
            {
                column++;
            }
            else if (c == '\t')
            {
                column += 4 - (column % 4);
            }
            else
            {
                break;
            }
        }

        return column;
    }

    // The extensions SymbolClassifier routes to its C-family rule (kept private there).
    private static bool IsCFamily(string path)
    {
        int dot = path.LastIndexOf('.');
        if (dot < 0 || dot == path.Length - 1)
        {
            return false;
        }

        int separator = path.LastIndexOfAny(['/', '\\']);
        if (dot < separator)
        {
            return false;
        }

        // net472: System.Range is not in the framework, so [dot..] is a Substring.
        return path.Substring(dot).ToLowerInvariant() is ".cs" or ".java" or ".cpp" or ".cc" or ".cxx"
            or ".c" or ".h" or ".hpp" or ".hxx";
    }

    // net472 port: [GeneratedRegex] is a net7+ source generator; a lazy static keeps the call
    // site (and the upstream diff) identical, Compiled keeps the perf intent.
    private static Regex? _relaxedMember;
    private static Regex RelaxedMemberRegex() => _relaxedMember ??= new Regex(
        @"^\s*(?<type>[A-Za-z_][\w<>\[\],.?]*)\s+(?<name>[A-Za-z_]\w*)\s*\(",
        RegexOptions.ExplicitCapture | RegexOptions.Compiled);
}
