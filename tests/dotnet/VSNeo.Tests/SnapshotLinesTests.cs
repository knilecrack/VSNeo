using Microsoft.VisualStudio.Text;
using VSNeo.Tests.Stubs;
using VSNeo_Extension.Nvim;
using Xunit;

namespace VSNeo.Tests;

/// <summary>
/// WriteSnapshotLines encodes a whole snapshot as an array of str lines without
/// materializing the lines. The contract under test: the bytes are exactly what
/// writing the same lines as strings would produce - empty lines, multi-byte
/// UTF-8, surrogate pairs, and a line that crosses the chunk size all included.
/// </summary>
public class SnapshotLinesTests
{
    [Fact]
    public void Snapshot_lines_encode_identically_to_string_lines()
    {
        var longLine = new string('x', 5000) + "é😀";
        var lines = new[] { "first", "", "héllo wörld", "emoji 😀 pair", longLine, "last" };
        var text = string.Join("\n", lines);
        ITextSnapshot snapshot = new StubSnapshot(text);

        var direct = new MsgPackWriter();
        direct.WriteSnapshotLines(snapshot);

        var shaped = new MsgPackWriter();
        shaped.WriteValue(lines);

        Assert.Equal(
            shaped.Buffer.AsSpan(0, shaped.Length).ToArray(),
            direct.Buffer.AsSpan(0, direct.Length).ToArray());
    }

    [Fact]
    public void Snapshot_lines_round_trip_through_the_reader()
    {
        var lines = new[] { "alpha", "", "😀" };
        ITextSnapshot snapshot = new StubSnapshot(string.Join("\n", lines));

        var w = new MsgPackWriter();
        w.WriteSnapshotLines(snapshot);

        var r = new MsgPackReader(w.Buffer, 0, w.Length);
        Assert.True(r.TryReadValue(out var value));
        Assert.Equal(lines, Assert.IsType<object[]>(value));
    }
}
