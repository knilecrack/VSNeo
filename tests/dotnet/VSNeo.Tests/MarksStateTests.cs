using System;
using System.IO;
using VSNeo_Extension.Nvim;
using Xunit;

namespace VSNeo.Tests;

/// <summary>
/// Per-file marks: normalized keys, case-insensitive lookup, wholesale
/// replacement per file.
/// </summary>
public class MarksStateTests
{
    private static string TempPath(string name) => Path.Combine(Path.GetTempPath(), name);

    [Fact]
    public void Marks_are_stored_by_normalized_path()
    {
        var marks = new MarksState();
        string path = TempPath("vsneo-marks-a.cs");
        string normalized = Path.GetFullPath(path);
        string? changed = null;
        marks.MarksChanged += p => changed = p;

        marks.HandleMarks(new object[]
        {
            path,
            new object[] { new object[] { 3L, "a" }, new object[] { 9L, "B" } },
        });

        Assert.Equal(normalized, changed);
        var list = marks.MarksFor(normalized);
        Assert.Equal(2, list.Count);
        Assert.Equal(3, list[0].Line);
        Assert.Equal("a", list[0].Name);
        Assert.Equal("B", list[1].Name);
    }

    [Fact]
    public void Lookup_is_case_insensitive_and_unknown_paths_are_empty()
    {
        var marks = new MarksState();
        string path = TempPath("vsneo-marks-b.cs");
        marks.HandleMarks(new object[]
        {
            path,
            new object[] { new object[] { 1L, "a" } },
        });

        Assert.Single(marks.MarksFor(Path.GetFullPath(path).ToUpperInvariant()));
        Assert.Empty(marks.MarksFor(TempPath("vsneo-marks-other.cs")));
        Assert.Empty(marks.MarksFor(null));
    }

    [Fact]
    public void A_file_s_marks_are_replaced_wholesale()
    {
        var marks = new MarksState();
        string path = TempPath("vsneo-marks-c.cs");
        marks.HandleMarks(new object[]
        {
            path,
            new object[] { new object[] { 1L, "a" }, new object[] { 2L, "b" } },
        });

        marks.HandleMarks(new object[]
        {
            path,
            new object[] { new object[] { 7L, "c" } },
        });

        BufferMark only = Assert.Single(marks.MarksFor(Path.GetFullPath(path)));
        Assert.Equal(7, only.Line);
        Assert.Equal("c", only.Name);
    }

    [Fact]
    public void An_empty_path_is_ignored()
    {
        int changes = 0;
        var marks = new MarksState();
        marks.MarksChanged += _ => changes++;

        marks.HandleMarks(new object[] { string.Empty, new object[0] });
        marks.HandleMarks(new object[0]);

        Assert.Equal(0, changes);
    }
}
