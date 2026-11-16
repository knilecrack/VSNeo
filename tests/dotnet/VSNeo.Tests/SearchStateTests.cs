using System.Collections.Generic;
using VSNeo_Extension.Nvim;
using Xunit;

namespace VSNeo.Tests;

/// <summary>
/// hlsearch matches, the whole-buffer count, the highlight colors and the yank
/// segments, driven with the same [args] shapes the companion sends.
/// </summary>
public class SearchStateTests
{
    private static object[] MatchArgs(params long[][] triples)
    {
        var items = new object[triples.Length];
        for (int i = 0; i < triples.Length; i++)
        {
            items[i] = new object[] { triples[i][0], triples[i][1], triples[i][2] };
        }

        return new object[] { items };
    }

    [Fact]
    public void Matches_are_parsed_and_replace_the_list()
    {
        int changes = 0;
        var search = new SearchState();
        search.MatchesChanged += () => changes++;

        search.HandleSearchMatches(MatchArgs(new long[] { 1, 2, 5 }, new long[] { 3, 0, 2 }));

        Assert.Equal(2, search.Matches.Count);
        Assert.Equal(1, search.Matches[0].Line);
        Assert.Equal(2, search.Matches[0].StartByte);
        Assert.Equal(5, search.Matches[0].EndByte);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void Identical_resends_do_not_rebuild_the_adornment()
    {
        int changes = 0;
        var search = new SearchState();
        search.MatchesChanged += () => changes++;

        object[] args = MatchArgs(new long[] { 1, 2, 5 });
        search.HandleSearchMatches(args);
        search.HandleSearchMatches(args);

        Assert.Equal(1, changes);

        search.HandleSearchMatches(MatchArgs(new long[] { 1, 2, 6 }));
        Assert.Equal(2, changes);
    }

    [Fact]
    public void Empty_matches_clear_the_count()
    {
        int changes = 0;
        var search = new SearchState();
        search.MatchesChanged += () => changes++;
        search.HandleSearchMatches(MatchArgs(new long[] { 1, 2, 5 }));
        search.HandleSearchCount(new object[] { 2L, 9L, 1L });

        search.HandleSearchMatches(new object[0]);

        Assert.Empty(search.Matches);
        Assert.Equal(0, search.MatchesBefore);
        Assert.Equal(-1, search.Total);
        Assert.False(search.TotalIncomplete);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void Count_and_colors_are_recorded()
    {
        var search = new SearchState();
        search.HandleSearchCount(new object[] { 2L, 9L, 1L });
        Assert.Equal(2, search.MatchesBefore);
        Assert.Equal(9, search.Total);
        Assert.True(search.TotalIncomplete);

        int changes = 0;
        search.HighlightsChanged += () => changes++;
        search.HandleHighlights(new object[] { 0x112233L, -1L, 0x445566L });

        Assert.Equal(0x112233, search.SearchColor);
        Assert.Equal(-1, search.CurrentMatchColor);
        Assert.Equal(0x445566, search.YankColor);
        Assert.True(search.CountEnabled);
        Assert.Equal(1, changes);

        // The optional fourth value turns the [n/N] chip off; three values
        // from an older companion leave it on.
        search.HandleHighlights(new object[] { 1L, 2L, 3L, 0L });
        Assert.False(search.CountEnabled);
    }

    [Fact]
    public void Yank_fires_segments_and_ignores_empty_payloads()
    {
        IReadOnlyList<SearchMatch>? flash = null;
        var search = new SearchState();
        search.YankFlashed += segments => flash = segments;

        search.HandleYank(new object[]
        {
            new object[] { new object[] { 4L, 1L, 3L }, new object[] { 5L, 0L, 2L } },
        });

        Assert.NotNull(flash);
        Assert.Equal(2, flash!.Count);
        Assert.Equal(4, flash[0].Line);

        search.HandleYank(new object[0]);
        Assert.Equal(2, flash.Count);
    }
}
