using VSNeo_Extension.Editor;
using Xunit;

namespace VSNeo.Tests;

/// <summary>
/// RemoteLineEdit.Narrow: a whole-line replace cut down to what changes. The
/// invariant is the buffer text afterwards; the rows pin where the edit sits.
/// </summary>
public class NarrowTests
{
    [Theory]
    // old line, new line, expected [start, end) within old, expected text
    [InlineData("    pu\r\n", "    pub\r\n", 6, 6, "b")]            // a typed letter
    [InlineData("    pub\r\n", "    pu\r\n", 6, 7, "")]             // a backspace
    [InlineData("    x\r\n", "    x\r\n    \r\n", 7, 7, "    \r\n")] // Enter: never splits CRLF
    [InlineData("aa\r\n", "aa\n", 2, 4, "\n")]                     // CRLF -> LF: the pair is replaced whole
    [InlineData("😀\n", "😀😀\n", 2, 2, "😀")]                       // surrogate pairs stay whole
    [InlineData("abc", "xyz", 0, 3, "xyz")]                        // nothing in common
    [InlineData("same\n", "same\n", 5, 5, "")]                     // no change at all
    public void Narrows_to_the_changed_characters(string oldText, string newText, int start, int end, string text)
    {
        int s = 0, e = oldText.Length;
        var t = newText;
        RemoteLineEdit.Narrow(oldText, ref s, ref e, ref t);

        Assert.Equal(newText, oldText.Substring(0, s) + t + oldText.Substring(e));
        Assert.Equal((start, end, text), (s, e, t));
    }
}
