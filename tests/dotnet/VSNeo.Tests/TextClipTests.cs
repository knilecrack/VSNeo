using System.Text;
using System.Text.Json;
using Xunit;

namespace VSNeo.Tests;

public class TextClipTests
{
    [Fact]
    public void Text_at_or_under_the_cap_is_untouched()
    {
        string line = new('a', TextClip.MaxChars);
        Assert.Same(line, TextClip.Text(line));
    }

    [Fact]
    public void Text_just_over_the_cap_is_cut_to_it()
    {
        string clipped = TextClip.Text(new string('a', TextClip.MaxChars + 1));
        Assert.Equal(TextClip.MaxChars, clipped.Length);
    }

    [Fact]
    public void A_surrogate_pair_across_the_cut_is_dropped_whole_and_the_json_writes()
    {
        // The emoji's high half is the last char under the cap, its low half the first over it.
        string line = new string('a', TextClip.MaxChars - 1) + "😀" + "tail";
        string clipped = TextClip.Text(line);

        Assert.Equal(TextClip.MaxChars - 1, clipped.Length);
        Assert.False(char.IsHighSurrogate(clipped[^1]));

        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("text", clipped);
            w.WriteEndObject();
        }

        Assert.Contains("\"text\"", Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    [Fact]
    public void A_whole_surrogate_pair_under_the_cap_is_kept()
    {
        string line = new string('a', TextClip.MaxChars - 2) + "😀" + "tail";
        Assert.Equal(TextClip.MaxChars, TextClip.Text(line).Length);
    }

    [Fact]
    public void A_range_across_the_cut_is_shortened_to_it()
    {
        var kept = TextClip.Ranges(new[] { new SeekyRange(10, 20), new SeekyRange(3990, 4010) }, 4000);
        Assert.Equal(new[] { new SeekyRange(10, 20), new SeekyRange(3990, 4000) }, kept);
    }

    [Fact]
    public void A_range_wholly_after_the_cut_is_dropped()
    {
        var kept = TextClip.Ranges(new[] { new SeekyRange(4000, 4005), new SeekyRange(5000, 5001) }, 4000);
        Assert.Empty(kept);
    }
}
