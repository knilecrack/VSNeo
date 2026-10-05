using System.Linq;
using VSNeo.Seeky;
using Xunit;

namespace VSNeo.Tests;

/// <summary>
/// The Seeky picker's Current File search. What matters to a Vim user: smart case like
/// 'smartcase', results in '/' order from the caret (so Enter on the preselected row is
/// "next match below"), and fuzzy words that must each match rather than one subsequence
/// that matches nearly every source line.
/// </summary>
public class LineSearchTests
{
    private static readonly string[] Lines =
    {
        "namespace Demo;",                     // 1
        "",                                    // 2
        "class TaskRunner",                    // 3
        "{",                                   // 4
        "    void Run() { task.Run(); }",      // 5
        "    // run the task",                 // 6
        "    int count = 0;",                  // 7
        "}",                                   // 8
    };

    private static int[] LinesOf(string query, string mode, int caret = 1) =>
        LineSearch.Search(Lines, query, mode, caret, 100, out _).Select(h => h.Line).ToArray();

    [Fact]
    public void Plain_is_smart_case()
    {
        Assert.Equal(new[] { 3, 5, 6 }, LinesOf("run", "plain"));
        Assert.Equal(new[] { 3, 5 }, LinesOf("Run", "plain"));
    }

    [Fact]
    public void Results_start_below_the_caret_and_wrap()
    {
        // The caret's own line comes last, as '/' reaches it only after wrapping.
        Assert.Equal(new[] { 6, 3, 5 }, LinesOf("run", "plain", caret: 5));
        Assert.Equal(new[] { 3, 5, 6 }, LinesOf("run", "plain", caret: 8));
    }

    [Fact]
    public void Every_occurrence_is_highlighted()
    {
        LineSearch.Hit hit = LineSearch.Search(Lines, "Run", "plain", 4, 100, out _).First();
        Assert.Equal(5, hit.Line);
        Assert.Equal(new[] { (9, 12), (22, 25) }, hit.Ranges);
    }

    [Fact]
    public void Regex_matches_and_bad_patterns_fall_back_to_literal()
    {
        Assert.Equal(new[] { 5, 7 }, LinesOf(@"^\s+(void|int)\b", "regex"));

        var hits = LineSearch.Search(Lines, "Run(", "regex", 1, 100, out string? error);
        Assert.NotNull(error);
        Assert.Equal(new[] { 5 }, hits.Select(h => h.Line).ToArray());
    }

    [Fact]
    public void Zero_width_regex_selects_without_highlighting()
    {
        LineSearch.Hit hit = LineSearch.Search(Lines, "^(?=namespace)", "regex", 8, 100, out _).Single();
        Assert.Equal(1, hit.Line);
        Assert.Empty(hit.Ranges);
    }

    [Fact]
    public void Fuzzy_words_must_each_match()
    {
        // "task" and "run" both appear only on lines 3, 5 and 6; "count" on 7 alone.
        Assert.Equal(new[] { 3, 5, 6 }, LinesOf("task run", "fuzzy").OrderBy(l => l).ToArray());
        Assert.Equal(new[] { 7 }, LinesOf("count", "fuzzy"));
        Assert.Empty(LinesOf("task count", "fuzzy"));
    }

    [Fact]
    public void Blank_queries_and_blank_lines_yield_nothing()
    {
        Assert.Empty(LinesOf("   ", "plain"));
        Assert.DoesNotContain(2, LinesOf(".*", "regex"));
    }

    [Fact]
    public void Results_are_capped()
    {
        Assert.Equal(2, LineSearch.Search(Lines, "run", "plain", 1, 2, out _).Count);
    }
}
