using Microsoft.CodeAnalysis.CSharp;
using VSNeo_Extension.Editor;
using Xunit;

namespace VSNeo.Tests;

/// <summary>
/// af/if/ac/ic and ]m [m ]M [M ]] [[ over a real Roslyn tree. Positions are
/// written as markers in the source: '|' is the cursor, removed before parsing.
/// Assertions read the selected text back, so they say what Vim would select.
/// </summary>
public class SyntaxTargetsTests
{
    private const string Source = """
        namespace N
        {
            /// <summary>The widget.</summary>
            public class Widget
            {
                private int _count;

                /// <summary>Adds.</summary>
                public int Add(int a, int b)
                {
                    var sum = a + b;
                    return sum;
                }

                public int Twice(int x) => x * 2;

                public void Run()
                {
                    System.Func<int, int> f = y => y + 1;
                    int Local(int z)
                    {
                        return z;
                    }
                }

                public int Count { get { return _count; } }
            }

            public struct Empty { }
        }
        """;

    private static (Microsoft.CodeAnalysis.SyntaxNode Root, string Text) Parse(string withCursor, out int cursor)
    {
        cursor = withCursor.IndexOf('|');
        var text = withCursor.Remove(cursor, 1);
        return (CSharpSyntaxTree.ParseText(text).GetRoot(), text);
    }

    private static string Select(string withCursor, string op, int count = 1)
    {
        var (root, text) = Parse(withCursor, out int cursor);
        var target = SyntaxTargets.Find(root, cursor, op, count);
        Assert.NotNull(target);
        return text.Substring(target!.Value.Start, target.Value.End - target.Value.Start);
    }

    private static string Cursor(string marker) => Source.Replace(marker, "|" + marker.Substring(0));

    [Fact]
    public void Af_is_the_whole_method_with_its_doc_comment_linewise()
    {
        var (root, _) = Parse(Source.Replace("var sum", "var |sum"), out int cursor);
        var t = SyntaxTargets.Find(root, cursor, "function_outer", 1)!.Value;
        Assert.True(t.Linewise);

        var s = Select(Source.Replace("var sum", "var |sum"), "function_outer");
        Assert.StartsWith("        /// <summary>Adds.</summary>", s);
        Assert.Contains("public int Add(int a, int b)", s);
        Assert.Contains("return sum;", s);
        Assert.Equal("        }", s.Substring(s.LastIndexOf('\n') + 1));
        Assert.DoesNotContain("Twice", s);
    }

    [Fact]
    public void If_is_the_lines_between_the_braces()
    {
        var s = Select(Source.Replace("return sum", "return |sum"), "function_inner");
        Assert.Contains("var sum = a + b;", s);
        Assert.Contains("return sum;", s);
        Assert.DoesNotContain("{", s);
        Assert.DoesNotContain("}", s);
    }

    [Fact]
    public void If_on_an_expression_bodied_method_is_the_expression()
    {
        var (root, _) = Parse(Source.Replace("=> x * 2", "=> x |* 2"), out int cursor);
        var t = SyntaxTargets.Find(root, cursor, "function_inner", 1)!.Value;
        Assert.False(t.Linewise);
        Assert.Equal("x * 2", Select(Source.Replace("=> x * 2", "=> x |* 2"), "function_inner"));
    }

    [Fact]
    public void The_innermost_function_wins_and_a_count_goes_outward()
    {
        var marked = Source.Replace("return z;", "return |z;");
        Assert.Contains("int Local(int z)", Select(marked, "function_outer"));
        Assert.DoesNotContain("public void Run()", Select(marked, "function_outer"));
        Assert.Contains("public void Run()", Select(marked, "function_outer", 2));
    }

    [Fact]
    public void A_lambda_inside_an_expression_is_characters()
    {
        var marked = Source.Replace("y => y + 1", "y => y |+ 1");
        var (root, _) = Parse(marked, out int cursor);
        var t = SyntaxTargets.Find(root, cursor, "function_outer", 1)!.Value;
        Assert.False(t.Linewise);
        Assert.Equal("y => y + 1", Select(marked, "function_outer"));
        Assert.Equal("y + 1", Select(marked, "function_inner"));
    }

    [Fact]
    public void An_accessor_is_a_function_too()
    {
        Assert.Equal("return _count;", Select(Source.Replace("return _count", "return |_count"), "function_inner"));
    }

    [Fact]
    public void Ac_is_the_whole_type_with_its_doc_comment_and_ic_its_members()
    {
        var marked = Source.Replace("private int _count", "private int |_count");
        var outer = Select(marked, "class_outer");
        Assert.StartsWith("    /// <summary>The widget.</summary>", outer);
        Assert.Contains("public class Widget", outer);

        var inner = Select(marked, "class_inner");
        Assert.StartsWith("        private int _count;", inner);
        Assert.DoesNotContain("public class Widget", inner);
    }

    [Fact]
    public void An_empty_body_is_no_target()
    {
        var (root, _) = Parse(Source.Replace("struct Empty { }", "struct |Empty { }"), out int cursor);
        Assert.Null(SyntaxTargets.Find(root, cursor, "class_inner", 1));
        Assert.NotNull(SyntaxTargets.Find(root, cursor, "class_outer", 1));
    }

    [Fact]
    public void Outside_any_function_is_no_target()
    {
        var (root, _) = Parse(Source.Replace("private int _count", "private |int _count"), out int cursor);
        Assert.Null(SyntaxTargets.Find(root, cursor, "function_outer", 1));
    }

    private static string LineAt(string text, int position)
    {
        int start = text.LastIndexOf('\n', System.Math.Max(0, position - 1)) + 1;
        int end = text.IndexOf('\n', position);
        return text.Substring(start, (end < 0 ? text.Length : end) - start).Trim();
    }

    private static string MotionLine(string withCursor, string op, int count = 1)
    {
        var (root, text) = Parse(withCursor, out int cursor);
        var t = SyntaxTargets.Find(root, cursor, op, count);
        Assert.NotNull(t);
        Assert.Equal(t!.Value.Start, t.Value.End);
        return LineAt(text, t.Value.Start);
    }

    [Fact]
    public void Bracket_m_walks_method_and_property_starts_and_skips_lambdas_and_accessors()
    {
        var marked = Source.Replace("private int _count", "|private int _count");
        Assert.Equal("public int Add(int a, int b)", MotionLine(marked, "function_next_start"));
        Assert.Equal("public int Twice(int x) => x * 2;", MotionLine(marked, "function_next_start", 2));
        Assert.Equal("public void Run()", MotionLine(marked, "function_next_start", 3));
        Assert.Equal("int Local(int z)", MotionLine(marked, "function_next_start", 4));
        Assert.Equal("public int Count { get { return _count; } }", MotionLine(marked, "function_next_start", 5));

        var fromCount = Source.Replace("public int Count", "|public int Count");
        Assert.Equal("int Local(int z)", MotionLine(fromCount, "function_prev_start"));

        var fromRun = Source.Replace("public void Run()", "|public void Run()");
        Assert.Equal("public int Twice(int x) => x * 2;", MotionLine(fromRun, "function_prev_start"));
    }

    [Fact]
    public void Bracket_M_lands_on_the_closing_brace_or_semicolon()
    {
        var marked = Source.Replace("var sum", "var |sum");
        var (root, text) = Parse(marked, out int cursor);
        var t = SyntaxTargets.Find(root, cursor, "function_next_end", 1)!.Value;
        Assert.Equal('}', text[t.Start]);
        Assert.Equal(';', text[SyntaxTargets.Find(root, cursor, "function_next_end", 2)!.Value.Start]);
    }

    [Fact]
    public void Double_brackets_walk_types()
    {
        var marked = Source.Replace("namespace N", "|namespace N");
        Assert.Equal("public class Widget", MotionLine(marked, "class_next_start"));
        Assert.Equal("public struct Empty { }", MotionLine(marked, "class_next_start", 2));
        var fromEmpty = Source.Replace("public struct Empty", "|public struct Empty");
        Assert.Equal("public class Widget", MotionLine(fromEmpty, "class_prev_start"));
    }

    [Fact]
    public void Past_the_last_target_is_no_target()
    {
        var (root, _) = Parse(Source.Replace("public struct Empty", "|public struct Empty"), out int cursor);
        Assert.Null(SyntaxTargets.Find(root, cursor, "class_next_start", 1));
        Assert.Null(SyntaxTargets.Find(root, cursor, "nonsense", 1));
    }
}
