using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace VSNeo_Extension.Editor
{
    /// <summary>A text object's range or a motion's target, in characters of the tree's text.</summary>
    internal readonly struct SyntaxTarget
    {
        public SyntaxTarget(int start, int end, bool linewise)
        {
            Start = start;
            End = end;
            Linewise = linewise;
        }

        /// <summary>First character.</summary>
        public int Start { get; }

        /// <summary>One past the last character; equal to Start for a motion target.</summary>
        public int End { get; }

        /// <summary>Whole lines (a declaration on lines of its own), else characters.</summary>
        public bool Linewise { get; }
    }

    /// <summary>
    /// Roslyn-exact text objects and motions - what nvim-treesitter-textobjects
    /// gives Neovim, from the language service's own tree instead of a grammar.
    /// Pure: a syntax root, a position and an operation in, a target out, so the
    /// .NET tests run it without Visual Studio. C# only; other languages (VB,
    /// and C++, whose service has no public tree) answer null.
    ///
    /// Operations ("count" is the depth for objects, the nth for motions):
    ///   function_outer / function_inner   af / if
    ///   class_outer / class_inner         ac / ic
    ///   function_next_start / function_prev_start   ]m / [m
    ///   function_next_end / function_prev_end       ]M / [M
    ///   class_next_start / class_prev_start         ]] / [[
    /// </summary>
    internal static class SyntaxTargets
    {
        public static SyntaxTarget? Find(SyntaxNode root, int position, string op, int count)
        {
            if (root == null || root.Language != LanguageNames.CSharp) return null;
            if (count < 1) count = 1;
            var text = root.SyntaxTree.GetText();
            position = position < 0 ? 0 : (position > text.Length ? text.Length : position);

            switch (op)
            {
                case "function_outer": return Outer(text, Enclosing(root, position, IsFunction), count);
                case "function_inner": return Inner(text, Enclosing(root, position, IsFunction), count);
                case "class_outer": return Outer(text, Enclosing(root, position, IsType), count);
                case "class_inner": return Inner(text, Enclosing(root, position, IsType), count);
                case "function_next_start": return Next(Starts(root, IsMember), position, count);
                case "function_prev_start": return Prev(Starts(root, IsMember), position, count);
                case "function_next_end": return Next(Ends(root, IsMember), position, count);
                case "function_prev_end": return Prev(Ends(root, IsMember), position, count);
                case "class_next_start": return Next(Starts(root, IsType), position, count);
                case "class_prev_start": return Prev(Starts(root, IsType), position, count);
                default: return null;
            }
        }

        // ---- what counts --------------------------------------------------

        /// <summary>af/if: methods, constructors, operators, local functions,
        /// accessors and lambdas - the innermost one wins, as in treesitter.</summary>
        private static bool IsFunction(SyntaxNode n) =>
            n is BaseMethodDeclarationSyntax
            || n is LocalFunctionStatementSyntax
            || n is AccessorDeclarationSyntax
            || n is AnonymousFunctionExpressionSyntax;

        /// <summary>]m/[m: the declarations a reader thinks of as methods -
        /// not lambdas or accessors, which would stop the motion inside one.</summary>
        private static bool IsMember(SyntaxNode n) =>
            n is BaseMethodDeclarationSyntax || n is LocalFunctionStatementSyntax;

        /// <summary>ac/ic and ]]/[[: classes, structs, interfaces, records, enums.</summary>
        private static bool IsType(SyntaxNode n) => n is BaseTypeDeclarationSyntax;

        // ---- objects -------------------------------------------------------

        /// <summary>The nodes around the position that match, innermost first.
        /// A position in a token's leading trivia counts as on the token only
        /// when nothing but indentation and doc comments lies between - the
        /// indentation before a declaration, its /// lines. A blank line or a
        /// plain comment between two members is in neither: FindToken hands
        /// those to the next member, and daf there deleted it.</summary>
        private static List<SyntaxNode> Enclosing(SyntaxNode root, int position, System.Func<SyntaxNode, bool> match)
        {
            var token = root.FindToken(position);
            if (token.Parent == null) return new List<SyntaxNode>();
            int anchor = position < token.SpanStart && AttachedTo(token, position) ? token.SpanStart : position;
            return token.Parent.AncestorsAndSelf()
                .Where(n => match(n) && n.Span.Start <= anchor && anchor < n.Span.End)
                .ToList();
        }

        private static bool AttachedTo(SyntaxToken token, int position)
        {
            foreach (var trivia in token.LeadingTrivia)
            {
                if (trivia.FullSpan.End <= position) continue;   // before the cursor
                if (!trivia.IsKind(SyntaxKind.WhitespaceTrivia)
                    && !trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
                    && !trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
                    return false;
            }
            return true;
        }

        private static SyntaxTarget? Outer(SourceText text, List<SyntaxNode> nodes, int depth)
        {
            if (nodes.Count < depth) return null;
            var node = nodes[depth - 1];

            // The XML doc comment belongs with its declaration: daf leaving an
            // orphan /// <summary> behind would be worse than Vim's own.
            int start = node.SpanStart;
            foreach (var trivia in node.GetLeadingTrivia())
            {
                if (trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
                    || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
                {
                    // FullSpan: the trivia's Span starts after the '///' marker.
                    start = trivia.FullSpan.Start;
                    break;
                }
            }
            return Range(text, start, node.Span.End);
        }

        private static SyntaxTarget? Inner(SourceText text, List<SyntaxNode> nodes, int depth)
        {
            if (nodes.Count < depth) return null;
            var node = nodes[depth - 1];

            switch (node)
            {
                case BaseTypeDeclarationSyntax type:
                    return BetweenBraces(text, type.OpenBraceToken, type.CloseBraceToken);
                case BaseMethodDeclarationSyntax method:
                    return Body(text, method.Body, method.ExpressionBody?.Expression);
                case LocalFunctionStatementSyntax local:
                    return Body(text, local.Body, local.ExpressionBody?.Expression);
                case AccessorDeclarationSyntax accessor:
                    return Body(text, accessor.Body, accessor.ExpressionBody?.Expression);
                case AnonymousFunctionExpressionSyntax lambda:
                    return Body(text, lambda.Block, lambda.ExpressionBody);
                default:
                    return null;
            }
        }

        private static SyntaxTarget? Body(SourceText text, BlockSyntax? block, ExpressionSyntax? expression)
        {
            if (block != null) return BetweenBraces(text, block.OpenBraceToken, block.CloseBraceToken);
            if (expression != null) return new SyntaxTarget(expression.SpanStart, expression.Span.End, linewise: false);
            return null;   // abstract, extern, partial without a body
        }

        /// <summary>Vim's i{: the lines strictly between the brace lines when
        /// each brace stands alone at its end of the body, else the characters
        /// between them. Line distance alone did not say that: `{ A();` over
        /// `B(); }` came back empty, and with a line between them the linewise
        /// body dropped A() and B(). Nothing between them is no target.</summary>
        private static SyntaxTarget? BetweenBraces(SourceText text, SyntaxToken open, SyntaxToken close)
        {
            if (open.IsMissing || close.IsMissing || open.Span.Length == 0 || close.Span.Length == 0) return null;

            int openLine = text.Lines.GetLineFromPosition(open.SpanStart).LineNumber;
            int closeLine = text.Lines.GetLineFromPosition(close.SpanStart).LineNumber;

            if (closeLine > openLine
                && IsBlank(text, open.Span.End, text.Lines[openLine].End)
                && IsBlank(text, text.Lines[closeLine].Start, close.SpanStart))
            {
                if (closeLine - openLine == 1) return null;   // '{' and '}' on adjacent lines: empty
                return new SyntaxTarget(text.Lines[openLine + 1].Start, text.Lines[closeLine - 1].End, linewise: true);
            }

            int start = open.Span.End, end = close.SpanStart;
            while (start < end && char.IsWhiteSpace(text[start])) start++;
            while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
            return end > start ? new SyntaxTarget(start, end, linewise: false) : (SyntaxTarget?)null;
        }

        private static bool IsBlank(SourceText text, int from, int to)
        {
            for (int i = from; i < to; i++)
                if (!char.IsWhiteSpace(text[i])) return false;
            return true;
        }

        /// <summary>Linewise when the range owns its lines - it starts at the
        /// first non-blank of its first line and ends at the last non-blank of
        /// its last - so daf removes the declaration's lines, not leaves its
        /// indentation and an empty line. A lambda inside an expression is
        /// characters.</summary>
        private static SyntaxTarget Range(SourceText text, int start, int end)
        {
            var first = text.Lines.GetLineFromPosition(start);
            var last = text.Lines.GetLineFromPosition(end > start ? end - 1 : end);

            bool ownsFirst = true;
            for (int i = first.Start; i < start; i++)
                if (!char.IsWhiteSpace(text[i])) { ownsFirst = false; break; }

            bool ownsLast = true;
            for (int i = end; i < last.End; i++)
                if (!char.IsWhiteSpace(text[i])) { ownsLast = false; break; }

            return ownsFirst && ownsLast
                ? new SyntaxTarget(first.Start, last.End, linewise: true)
                : new SyntaxTarget(start, end, linewise: false);
        }

        // ---- motions -------------------------------------------------------

        private static List<int> Starts(SyntaxNode root, System.Func<SyntaxNode, bool> match) =>
            root.DescendantNodes().Where(match).Select(n => n.SpanStart).Distinct().OrderBy(p => p).ToList();

        /// <summary>The last character of each declaration (its '}' or ';').</summary>
        private static List<int> Ends(SyntaxNode root, System.Func<SyntaxNode, bool> match) =>
            root.DescendantNodes().Where(match).Select(n => n.Span.End - 1).Distinct().OrderBy(p => p).ToList();

        private static SyntaxTarget? Next(List<int> positions, int position, int count)
        {
            int seen = 0;
            foreach (var p in positions)
                if (p > position && ++seen == count) return new SyntaxTarget(p, p, linewise: false);
            return null;
        }

        private static SyntaxTarget? Prev(List<int> positions, int position, int count)
        {
            int seen = 0;
            for (int i = positions.Count - 1; i >= 0; i--)
                if (positions[i] < position && ++seen == count) return new SyntaxTarget(positions[i], positions[i], linewise: false);
            return null;
        }
    }
}
