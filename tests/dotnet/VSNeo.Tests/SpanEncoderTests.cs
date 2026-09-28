using VSNeo_Extension.Nvim;
using Xunit;

namespace VSNeo.Tests;

/// <summary>
/// SpanEncoder streams an apply_spans span into the frame where the edit path
/// used to build an object[] and hand it to WriteValue. The contract under
/// test: the bytes are exactly what the object shape produced - nvim cannot
/// tell the difference - for a typed character, a deletion, every line-break
/// flavor, and multi-byte text.
/// </summary>
public class SpanEncoderTests
{
    private static byte[] ObjectShape(int startRow, int startCol, int endRow, int endCol, string newText)
    {
        // What BufferMirror.ToSpan built: four boxed ints and the replacement
        // as Split's string[] (a single empty string for a deletion).
        var w = new MsgPackWriter();
        w.WriteValue(new object[]
        {
            startRow, startCol, endRow, endCol,
            string.IsNullOrEmpty(newText)
                ? (object)new object[] { string.Empty }
                : newText.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None),
        });
        return w.Buffer.AsSpan(0, w.Length).ToArray();
    }

    private static byte[] Streamed(int startRow, int startCol, int endRow, int endCol, string newText)
    {
        var w = new MsgPackWriter();
        SpanEncoder.WriteSpan(w, startRow, startCol, endRow, endCol, newText);
        return w.Buffer.AsSpan(0, w.Length).ToArray();
    }

    [Theory]
    [InlineData("x")]
    [InlineData("")]
    [InlineData("two\nlines")]
    [InlineData("windows\r\nline\r\nendings\r\n")]
    [InlineData("lone\rcarriage\rreturns")]
    [InlineData("trailing\n")]
    [InlineData("\nleading")]
    [InlineData("\r\n")]
    [InlineData("a\n\nb")]
    [InlineData("héllo wörld 😀")]
    public void Spans_encode_identically_to_the_object_shape(string newText) =>
        Assert.Equal(ObjectShape(3, 4, 5, 9, newText), Streamed(3, 4, 5, 9, newText));

    [Fact]
    public void Rows_and_columns_encode_as_their_int_widths()
    {
        // Line numbers past the fixint range take the wide forms; both sides
        // must agree on every one of them.
        Assert.Equal(
            ObjectShape(123456, 300, 123457, 0, "x"),
            Streamed(123456, 300, 123457, 0, "x"));
    }
}
