using System.Collections.Generic;
using VSNeo_Extension.Infrastructure;
using Xunit;

namespace VSNeo.Tests;

/// <summary>
/// Screen-row arithmetic over collapsed regions. A collapsed region from line
/// s to line e keeps s on screen and hides s+1..e, in Visual Studio and in
/// nvim's mirrored folds alike; the viewport sync counts in these rows so its
/// edge, centre and end-of-file decisions match what nvim's window does.
/// </summary>
public class FoldRowsTests
{
    private static FoldRows Folds(params (int start, int end)[] regions)
    {
        var list = new List<KeyValuePair<int, int>>();
        foreach (var r in regions) list.Add(new KeyValuePair<int, int>(r.start, r.end));
        return FoldRows.FromRegions(list);
    }

    [Fact]
    public void Without_folds_rows_are_lines()
    {
        Assert.Equal(10, FoldRows.None.RowsBetween(5, 15));
        Assert.Equal(-10, FoldRows.None.RowsBetween(15, 5));
        Assert.Equal(5, FoldRows.None.LineRowsAbove(15, 10));
        Assert.Equal(0, FoldRows.None.LineRowsAbove(3, 10));
    }

    [Fact]
    public void A_collapsed_region_is_one_row()
    {
        var folds = Folds((20, 39));
        Assert.Equal(1, folds.RowsBetween(20, 40));    // header row, then line 40
        Assert.Equal(13, folds.RowsBetween(8, 40));    // 8..19 is 12 rows, the fold 1
        Assert.Equal(0, folds.RowsBetween(20, 35));    // a hidden line sits on its header's row
        Assert.True(folds.IsHidden(21));
        Assert.False(folds.IsHidden(20));
        Assert.False(folds.IsHidden(40));
        Assert.Equal(20, folds.VisibleLineOf(33));
    }

    [Fact]
    public void Walking_up_skips_a_fold_in_one_row()
    {
        var folds = Folds((20, 39));
        Assert.Equal(20, folds.LineRowsAbove(40, 1));
        Assert.Equal(19, folds.LineRowsAbove(40, 2));
        Assert.Equal(20, folds.LineRowsAbove(33, 0));  // from inside: the header
    }

    [Fact]
    public void Nested_and_overlapping_regions_hide_their_lines_once()
    {
        var folds = Folds((10, 30), (12, 15), (28, 40));
        Assert.Equal(1, folds.RowsBetween(10, 41));
        Assert.Equal(10, folds.LineRowsAbove(41, 1));
    }

    [Fact]
    public void Adjacent_regions_merge_and_one_line_regions_hide_nothing()
    {
        var folds = Folds((10, 20), (21, 30), (50, 50));
        Assert.Equal(2, folds.RowsBetween(10, 31));    // 10 and 21 are headers, then 31
        Assert.Equal(3, folds.RowsBetween(49, 52));
        Assert.False(folds.IsHidden(50));
    }
}
