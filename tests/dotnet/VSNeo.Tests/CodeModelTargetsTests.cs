using System.Collections.Generic;
using VSNeo_Extension.Editor;
using Xunit;
using static VSNeo_Extension.Editor.CodeModelTargets;

namespace VSNeo.Tests;

/// <summary>
/// The code-model fallback (C++): a list of functions and types with their
/// ranges, as Visual Studio's FileCodeModel reports them, in; the same
/// targets as the Roslyn path out.
/// </summary>
public class CodeModelTargetsTests
{
    private const string Cpp =
        "#include <vector>\n" +                       // 0
        "\n" +                                        // 1
        "namespace geo {\n" +                         // 2
        "\n" +                                        // 3
        "class Point {\n" +                           // 4
        "public:\n" +                                 // 5
        "    int x;\n" +                              // 6
        "    int Sum() const {\n" +                   // 7
        "        int s = x + y;\n" +                  // 8
        "        return s;\n" +                       // 9
        "    }\n" +                                   // 10
        "    int Zero() const { return 0; }\n" +      // 11
        "    int y;\n" +                              // 12
        "};\n" +                                      // 13
        "\n" +                                        // 14
        "int Free(int a);\n" +                        // 15
        "\n" +                                        // 16
        "struct Empty {\n" +                          // 17
        "};\n" +                                      // 18
        "}\n";                                        // 19

    /// <summary>[start of the first marker, end of the last marker) - what the code model reports.</summary>
    private static Element E(Kind kind, string from, string through)
    {
        int s = Cpp.IndexOf(from);
        int e = Cpp.IndexOf(through, s) + through.Length;
        return new Element(kind, s, e);
    }

    private static readonly List<Element> Elements = new()
    {
        E(Kind.Type, "class Point", "};"),
        E(Kind.Function, "int Sum()", "return s;\n    }"),
        E(Kind.Function, "int Zero()", "return 0; }"),
        E(Kind.Function, "int Free(int a);", "int Free(int a);"),
        E(Kind.Type, "struct Empty", "};\n}"[..2]),
    };

    private static int At(string marker) => Cpp.IndexOf(marker);

    private static string Text(SyntaxTarget t) => Cpp.Substring(t.Start, t.End - t.Start);

    private static int Line(int position)
    {
        int line = 0;
        for (int i = 0; i < position; i++) if (Cpp[i] == '\n') line++;
        return line;
    }

    [Fact]
    public void Af_is_the_method_linewise()
    {
        var t = Find(Cpp, Elements, At("return s;"), "function_outer", 1)!.Value;
        Assert.True(t.Linewise);
        Assert.Equal("    int Sum() const {\n        int s = x + y;\n        return s;\n    }", Text(t));
    }

    [Fact]
    public void If_is_the_lines_inside_the_braces()
    {
        var t = Find(Cpp, Elements, At("int s = x"), "function_inner", 1)!.Value;
        Assert.True(t.Linewise);
        Assert.Equal("        int s = x + y;\n        return s;", Text(t));
    }

    [Fact]
    public void If_on_a_one_line_body_is_the_characters_between_the_braces()
    {
        var t = Find(Cpp, Elements, At("return 0"), "function_inner", 1)!.Value;
        Assert.False(t.Linewise);
        Assert.Equal("return 0;", Text(t));
    }

    [Fact]
    public void A_prototype_has_no_inner_and_an_outer_line()
    {
        Assert.Null(Find(Cpp, Elements, At("Free(int"), "function_inner", 1));
        Assert.Equal("int Free(int a);", Text(Find(Cpp, Elements, At("Free(int"), "function_outer", 1)!.Value));
    }

    [Fact]
    public void The_indentation_before_a_method_counts_as_inside_it()
    {
        int indent = Cpp.IndexOf("    int Sum()");
        Assert.Contains("int Sum()", Text(Find(Cpp, Elements, indent, "function_outer", 1)!.Value));
    }

    [Fact]
    public void Ac_and_ic_are_the_class_and_its_members()
    {
        var outer = Find(Cpp, Elements, At("int x;"), "class_outer", 1)!.Value;
        Assert.True(outer.Linewise);
        Assert.StartsWith("class Point {", Text(outer));
        Assert.EndsWith("};", Text(outer));

        var inner = Find(Cpp, Elements, At("int x;"), "class_inner", 1)!.Value;
        Assert.StartsWith("public:", Text(inner));
        Assert.EndsWith("    int y;", Text(inner));
    }

    [Fact]
    public void An_empty_type_has_no_inner()
    {
        Assert.Null(Find(Cpp, Elements, At("struct Empty"), "class_inner", 1));
    }

    [Fact]
    public void Inside_a_method_the_class_is_the_second_level()
    {
        Assert.StartsWith("class Point", Text(Find(Cpp, Elements, At("return s;"), "class_outer", 1)!.Value));
        Assert.Null(Find(Cpp, Elements, At("return s;"), "function_outer", 2));
    }

    [Fact]
    public void Bracket_m_walks_the_functions_and_bracket_bracket_the_types()
    {
        int top = At("#include");
        Assert.Equal(7, Line(Find(Cpp, Elements, top, "function_next_start", 1)!.Value.Start));
        Assert.Equal(11, Line(Find(Cpp, Elements, top, "function_next_start", 2)!.Value.Start));
        Assert.Equal(15, Line(Find(Cpp, Elements, top, "function_next_start", 3)!.Value.Start));
        Assert.Equal(11, Line(Find(Cpp, Elements, At("int Free"), "function_prev_start", 1)!.Value.Start));
        Assert.Equal('}', Cpp[Find(Cpp, Elements, At("int s = x"), "function_next_end", 1)!.Value.Start]);
        Assert.Equal(4, Line(Find(Cpp, Elements, top, "class_next_start", 1)!.Value.Start));
        Assert.Equal(17, Line(Find(Cpp, Elements, top, "class_next_start", 2)!.Value.Start));
        Assert.Null(Find(Cpp, Elements, At("struct Empty"), "class_next_start", 1));
    }

    [Fact]
    public void Outside_every_element_is_no_target()
    {
        Assert.Null(Find(Cpp, Elements, At("#include"), "function_outer", 1));
        Assert.Null(Find(Cpp, Elements, At("#include"), "nonsense", 1));
    }
}
