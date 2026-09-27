using System.Text;
using VSNeo.Tests.Stubs;
using VSNeo_Extension.Infrastructure;
using Xunit;

namespace VSNeo.Tests;

/// <summary>
/// nvim speaks UTF-8 byte columns, Visual Studio UTF-16 char columns, and
/// ColumnMapper is the one place that converts. The oracle is
/// Encoding.UTF8.GetByteCount over the prefix. The snapshot-line overloads
/// (the allocation-free ones the key path uses) must agree with the string
/// overloads at every offset.
/// </summary>
public class ColumnMapperTests
{
    public static TheoryData<string> Lines => new()
    {
        "",
        "plain ascii",
        "café au lait",          // 2-byte Latin
        "naïve façade über",     // several 2-byte
        "Привет, мир",           // 2-byte Cyrillic (U+0400..U+04FF)
        "Ωμέγα שלום مرحبا",      // 2-byte Greek (below U+0400), Hebrew and Arabic (U+05xx, U+06xx)
        "日本語のテキスト",        // 3-byte CJK
        "a😀b",                  // surrogate pair, 4 bytes
        "😀😀",                  // adjacent pairs
        "x = \"héllo 👋🏽 wörld\";", // skin-tone modifier: two pairs
        "\ttab\tseparated",
    };

    /// <summary>Char offsets that fall on code point boundaries.</summary>
    private static IEnumerable<int> Boundaries(string s)
    {
        for (int i = 0; i <= s.Length; i++)
            if (i == 0 || i == s.Length || !char.IsLowSurrogate(s[i]))
                yield return i;
    }

    private static int Bytes(string s, int chars) => Encoding.UTF8.GetByteCount(s.AsSpan(0, chars));

    [Theory, MemberData(nameof(Lines))]
    public void CharToByte_matches_utf8_at_every_boundary(string s)
    {
        var line = StubSnapshot.Line(s);
        foreach (int k in Boundaries(s))
        {
            Assert.Equal(Bytes(s, k), ColumnMapper.CharToByte(s, k));
            Assert.Equal(Bytes(s, k), ColumnMapper.CharToByte(line, k));
        }
    }

    [Theory, MemberData(nameof(Lines))]
    public void ByteToChar_inverts_CharToByte_at_every_boundary(string s)
    {
        var line = StubSnapshot.Line(s);
        foreach (int k in Boundaries(s))
        {
            int b = Bytes(s, k);
            Assert.Equal(k, ColumnMapper.ByteToChar(s, b));
            Assert.Equal(k, ColumnMapper.ByteToChar(line, b));
        }
    }

    [Theory, MemberData(nameof(Lines))]
    public void Snapshot_overloads_agree_with_string_overloads_everywhere(string s)
    {
        // Every offset, boundaries or not, including past the end: whatever
        // the string versions answer, the allocation-free ones must too.
        var line = StubSnapshot.Line(s);
        for (int k = -1; k <= s.Length + 2; k++)
            Assert.Equal(ColumnMapper.CharToByte(s, k), ColumnMapper.CharToByte(line, k));
        int total = Encoding.UTF8.GetByteCount(s);
        for (int b = -1; b <= total + 2; b++)
            Assert.Equal(ColumnMapper.ByteToChar(s, b), ColumnMapper.ByteToChar(line, b));
    }

    [Fact]
    public void A_byte_offset_inside_a_character_rounds_up_to_the_next_character()
    {
        // é is two bytes; byte 1 is inside it.
        Assert.Equal(1, ColumnMapper.ByteToChar("é", 1));
        // 😀 is four bytes and two UTF-16 units; bytes 1-3 are inside it.
        for (int b = 1; b <= 3; b++)
            Assert.Equal(2, ColumnMapper.ByteToChar("😀x", b));
    }

    [Fact]
    public void Offsets_past_the_end_clamp_to_the_line()
    {
        Assert.Equal(3, ColumnMapper.ByteToChar("abc", 99));
        Assert.Equal(3, ColumnMapper.CharToByte("abc", 99));
        Assert.Equal(0, ColumnMapper.ByteToChar("abc", -5));
        Assert.Equal(0, ColumnMapper.CharToByte("abc", -5));
    }

    [Fact]
    public void A_lone_surrogate_counts_as_three_bytes_like_its_utf8_replacement()
    {
        // What UTF8Encoding emits for an unpaired surrogate (U+FFFD).
        Assert.Equal(3, ColumnMapper.CharToByte("\ud83d", 1));
        Assert.Equal(4, ColumnMapper.CharToByte("\ud83dx", 2));
    }
}
