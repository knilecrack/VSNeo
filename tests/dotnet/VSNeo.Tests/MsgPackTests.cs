using System.Text;
using VSNeo_Extension.Nvim;
using Xunit;

namespace VSNeo.Tests;

/// <summary>
/// The hand-rolled msgpack subset nvim's RPC uses. Contracts under test: every
/// integer reads back as long, strings as string, maps as
/// Dictionary&lt;string, object?&gt;, EXT as NvimHandle; a truncated value is
/// "not yet" (false), never an exception; skipping consumes exactly what
/// reading does.
/// </summary>
public class MsgPackTests
{
    private static byte[] Encode(object? value)
    {
        var w = new MsgPackWriter();
        w.WriteValue(value!);
        return w.Buffer.AsSpan(0, w.Length).ToArray();
    }

    private static object? Decode(byte[] bytes)
    {
        var r = new MsgPackReader(bytes, 0, bytes.Length);
        Assert.True(r.TryReadValue(out var value));
        Assert.Equal(bytes.Length, r.Position);
        return value;
    }

    public static TheoryData<long> Integers => new()
    {
        0, 1, 127, 128, 255, 256, 65535, 65536, uint.MaxValue, (long)uint.MaxValue + 1, long.MaxValue,
        -1, -31, -32, -33, -128, -129, short.MinValue, short.MinValue - 1, int.MinValue, (long)int.MinValue - 1, long.MinValue,
    };

    [Theory, MemberData(nameof(Integers))]
    public void Integers_round_trip_as_long_at_every_width_boundary(long v)
    {
        Assert.Equal(v, Assert.IsType<long>(Decode(Encode(v))));
    }

    [Fact]
    public void Narrow_integer_types_widen_to_long()
    {
        Assert.Equal(7L, Decode(Encode(7)));
        Assert.Equal(7L, Decode(Encode((short)7)));
        Assert.Equal(7L, Decode(Encode((byte)7)));
        Assert.Equal(7L, Decode(Encode(7u)));
    }

    [Fact]
    public void Nil_booleans_and_floats_round_trip()
    {
        Assert.Null(Decode(Encode(null)));
        Assert.Equal(true, Decode(Encode(true)));
        Assert.Equal(false, Decode(Encode(false)));
        Assert.Equal(1.5, Decode(Encode(1.5)));
        Assert.Equal(-0.25, Decode(Encode(-0.25f)));
    }

    public static TheoryData<string> Strings => new()
    {
        "", "a", new string('x', 31), new string('x', 32), new string('x', 255), new string('x', 256),
        new string('y', 65536), "héllo wörld", "日本語", "emoji 😀👋🏽", "line\nbreak\ttab",
    };

    [Theory, MemberData(nameof(Strings))]
    public void Strings_round_trip_across_every_length_header(string s)
    {
        Assert.Equal(s, Decode(Encode(s)));
    }

    [Fact]
    public void String_length_header_counts_utf8_bytes_not_chars()
    {
        // 16 two-byte chars = 32 bytes: past fixstr's 31, so str8.
        var bytes = Encode(new string('é', 16));
        Assert.Equal(0xd9, bytes[0]);
        Assert.Equal(32, bytes[1]);
    }

    [Fact]
    public void Binary_round_trips_as_bytes()
    {
        var data = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();
        Assert.Equal(data, Assert.IsType<byte[]>(Decode(Encode(data))));
    }

    [Fact]
    public void Arrays_and_maps_round_trip_nested()
    {
        var map = new Dictionary<string, object?>
        {
            ["mode"] = "n",
            ["lines"] = new object[] { "a", "b", new object[] { 1L, 2L } },
            ["nothing"] = null,
        };
        var back = Assert.IsType<Dictionary<string, object?>>(Decode(Encode(map)));
        Assert.Equal("n", back["mode"]);
        Assert.Null(back["nothing"]);
        var lines = Assert.IsType<object[]>(back["lines"]);
        Assert.Equal("b", lines[1]);
        Assert.Equal(new object[] { 1L, 2L }, Assert.IsType<object[]>(lines[2]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(70000)]
    public void Array_headers_switch_width_at_the_right_counts(int count)
    {
        var array = Enumerable.Repeat<object>(1L, count).ToArray();
        Assert.Equal(count, Assert.IsType<object[]>(Decode(Encode(array))).Length);
    }

    [Fact]
    public void Unsupported_types_throw_instead_of_encoding_something_wrong()
    {
        Assert.Throws<NotSupportedException>(() => Encode(new object()));
    }

    [Theory]
    [InlineData(new byte[] { 0xd4, 0x00, 0x05 }, 0, 5)]                   // fixext1: Buffer 5
    [InlineData(new byte[] { 0xd5, 0x01, 0xcc, 0xe8 }, 1, 232)]                        // fixext2: Window, uint8 payload
    [InlineData(new byte[] { 0xc7, 0x03, 0x01, 0xcd, 0x03, 0xe8 }, 1, 1000)]           // ext8: Window, uint16 payload
    [InlineData(new byte[] { 0xc7, 0x05, 0x02, 0xce, 0x00, 0x01, 0x00, 0x00 }, 2, 65536)] // ext8: Tabpage, uint32 payload
    public void Ext_values_decode_to_nvim_handles(byte[] bytes, int kind, long id)
    {
        var r = new MsgPackReader(bytes, 0, bytes.Length);
        Assert.True(r.TryReadValue(out var value));
        var handle = Assert.IsType<NvimHandle>(value);
        Assert.Equal((sbyte)kind, handle.Kind);
        Assert.Equal(id, handle.Id);
        Assert.Equal(bytes.Length, r.Position);
    }

    public static TheoryData<byte[]> Samples => new()
    {
        Encode(42L), Encode(-1000L), Encode(long.MinValue), Encode("héllo 😀"), Encode(new string('z', 300)),
        Encode(new byte[] { 1, 2, 3 }), Encode(3.25),
        Encode(new object[] { 0L, 7L, "nvim_input", new object[] { "jj" } }),
        Encode(new Dictionary<string, object?> { ["k"] = new object[] { true, null, 1.0 } }),
        new byte[] { 0xc7, 0x03, 0x01, 0xcd, 0x03, 0xe8 },
    };

    [Theory, MemberData(nameof(Samples))]
    public void Every_truncation_is_not_yet_never_an_exception(byte[] bytes)
    {
        for (int cut = 0; cut < bytes.Length; cut++)
        {
            var r = new MsgPackReader(bytes, 0, cut);
            Assert.False(r.TryReadValue(out _));
            var s = new MsgPackReader(bytes, 0, cut);
            Assert.False(s.TrySkipValue());
        }
    }

    [Theory, MemberData(nameof(Samples))]
    public void Skipping_consumes_exactly_what_reading_does(byte[] bytes)
    {
        var padded = bytes.Concat(new byte[] { 0xc0, 0xc0 }).ToArray();
        var read = new MsgPackReader(padded, 0, padded.Length);
        var skip = new MsgPackReader(padded, 0, padded.Length);
        Assert.True(read.TryReadValue(out _));
        Assert.True(skip.TrySkipValue());
        Assert.Equal(bytes.Length, read.Position);
        Assert.Equal(read.Position, skip.Position);
    }

    [Fact]
    public void Reading_respects_a_nonzero_start_offset()
    {
        var inner = Encode("mid");
        var bytes = new byte[] { 0xff, 0xff }.Concat(inner).ToArray();
        var r = new MsgPackReader(bytes, 2, bytes.Length);
        Assert.True(r.TryReadValue(out var v));
        Assert.Equal("mid", v);
    }
}

public class MsgPackRequestFrameTests
{
    [Fact]
    public void WriteRequestFrame_matches_the_array_shaped_encoding()
    {
        object[] args = { 7L, "x", new object[] { 1L, 2L } };

        var direct = new MsgPackWriter();
        direct.WriteRequestFrame(42u, "nvim_exec_lua", args);

        var shaped = new MsgPackWriter();
        shaped.WriteValue(new object[] { 0, 42L, "nvim_exec_lua", args });

        Assert.Equal(
            shaped.Buffer.AsSpan(0, shaped.Length).ToArray(),
            direct.Buffer.AsSpan(0, direct.Length).ToArray());
    }

    [Fact]
    public void WriteRequestFrame_round_trips_as_a_request()
    {
        var w = new MsgPackWriter();
        w.WriteRequestFrame(9u, "nvim_input", new object[] { "j" });

        var r = new MsgPackReader(w.Buffer, 0, w.Length);
        Assert.True(r.TryReadValue(out var value));
        var frame = Assert.IsType<object[]>(value);
        Assert.Equal(4, frame.Length);
        Assert.Equal(0L, frame[0]);
        Assert.Equal(9L, frame[1]);
        Assert.Equal("nvim_input", frame[2]);
        Assert.Equal(new object[] { "j" }, Assert.IsType<object[]>(frame[3]));
    }
}
