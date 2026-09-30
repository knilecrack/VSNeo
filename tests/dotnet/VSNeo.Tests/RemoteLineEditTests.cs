using VSNeo.Tests.Stubs;
using VSNeo_Extension.Editor;
using Xunit;

namespace VSNeo.Tests;

/// <summary>
/// The nvim -> Visual Studio line model. Each row is one nvim_buf_lines_event
/// (first, last, replacement) against a buffer, and the assertion that matters
/// is the last one: applying the planned span and text to the buffer must
/// leave exactly nvim's lines joined by the line break - VS line i is nvim
/// line i, or the mirror's drift repair resends the wrong side.
/// </summary>
public class RemoteLineEditTests
{
    public static TheoryData<string, int, int, string[], string> Cases => new()
    {
        // before                first last replacement    expected (nvim's lines joined)
        { "a\nb",                1, 2, [],                 "a" },              // dd on the last line, no trailing newline
        { "a\nb",                1, 2, ["x"],              "a\nx" },           // cc on the last line
        { "",                    1, 1, [""],               "\n" },             // o in an empty buffer
        { "a\nb",                0, 2, [],                 "" },               // ggdG
        { "a\nb",                0, 2, [""],               "" },               // :%d leaves [""]
        { "a\nb\n",              2, 3, [],                 "a\nb" },           // Gdd with a trailing newline
        { "a\nb\n",              1, 2, [],                 "a\n" },            // dd on b, trailing newline kept
        { "a\nb\n",              3, 3, [""],               "a\nb\n\n" },       // o on the trailing empty line
        { "a\nb",                2, 2, ["c"],              "a\nb\nc" },        // o on the last line
        { "foo\n\nbar",          1, 1, [""],               "foo\n\n\nbar" },   // o above a blank line
        { "foo\nbar",            1, 1, ["x"],              "foo\nx\nbar" },    // yyP
        { "a\nb\nc",             0, 1, ["x", "y"],         "x\ny\nb\nc" },     // a set_text echo shape
        { "a",                   0, 1, [],                 "" },               // dd on the only line
        { "a\nb\nc",             1, 2, ["B"],              "a\nB\nc" },        // replace a middle line
        { "a\nb\nc",             0, 3, ["x"],              "x" },              // whole buffer replaced
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Applying_the_plan_reproduces_nvims_lines(
        string before, int first, int last, string[] replacement, string expected)
    {
        var snapshot = new StubSnapshot(before);

        RemoteLineEdit.Plan(snapshot, first, last, replacement, "\n",
                            out int start, out int end, out string text);

        Assert.InRange(start, 0, before.Length);
        Assert.InRange(end, start, before.Length);
        Assert.Equal(expected, before.Substring(0, start) + text + before.Substring(end));
    }

    [Fact]
    public void A_pure_insert_replaces_nothing()
    {
        var snapshot = new StubSnapshot("foo\n\nbar");

        RemoteLineEdit.Plan(snapshot, 1, 1, [""], "\n", out int start, out int end, out string text);

        Assert.Equal(start, end);
        Assert.Equal("\n", text);
    }

    [Fact]
    public void Deleting_the_tail_eats_the_preceding_break()
    {
        var snapshot = new StubSnapshot("a\nb");

        RemoteLineEdit.Plan(snapshot, 1, 2, [], "\n", out int start, out int end, out string text);

        Assert.Equal(1, start);   // right after "a", on its line break
        Assert.Equal(3, end);
        Assert.Equal("", text);
    }
}
