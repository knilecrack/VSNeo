using VSNeo_Extension.Nvim;
using Xunit;

namespace VSNeo.Tests;

/// <summary>
/// MsgPackStreamReader frames nvim's byte stream into whole RPC messages. The
/// pipe hands data over in arbitrary pieces, so frames must survive any split,
/// back-to-back frames must come out one at a time, a frame bigger than the
/// buffer must grow it, and redraw batches the hub never reads are dropped
/// without being decoded. The two fixed shapes on the wire - a vsneo_state
/// push and a response - take fast paths that skip the frame array; a split
/// response falls back to the generic decode and arrives framed.
/// </summary>
public class FrameReaderTests
{
    private static byte[] Encode(object value)
    {
        var w = new MsgPackWriter();
        w.WriteValue(value);
        return w.Buffer.AsSpan(0, w.Length).ToArray();
    }

    /// <summary>Hands out at most <c>chunk</c> bytes per read, like a busy pipe.</summary>
    private sealed class ChunkedStream(byte[] data, int chunk) : Stream
    {
        private int _pos;
        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = Math.Min(Math.Min(count, chunk), data.Length - _pos);
            Array.Copy(data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_corrupt_top_level_array_count_waits_for_bytes_instead_of_allocating()
    {
        // array32 of 16M elements with no bytes behind it: the frame reader used
        // to size its item array (128 MB) from the header alone.
        var data = new byte[] { 0xdd, 0x00, 0xff, 0xff, 0xff, 0x01 };
        long before = GC.GetTotalAllocatedBytes(true);
        try { await ReadAll(data, 64); }
        catch (Exception e) when (e is InvalidDataException or IOException) { }
        Assert.True(GC.GetTotalAllocatedBytes(true) - before < 16_000_000);
    }

    private static async Task<List<MsgPackStreamReader.ReadResult>> ReadAll(byte[] data, int chunk)
    {
        var items = new List<MsgPackStreamReader.ReadResult>();
        using var reader = new MsgPackStreamReader(new ChunkedStream(data, chunk));
        while (true)
        {
            var result = await reader.ReadAsync(CancellationToken.None);
            if (result.IsEmpty) break;
            items.Add(result);
        }
        return items;
    }

    private static object[] FrameOf(MsgPackStreamReader.ReadResult item)
    {
        Assert.Null(item.State);
        Assert.Null(item.Response);
        return Assert.IsType<object[]>(item.Frame);
    }

    private static MsgPackStreamReader.NvimResponse ResponseOf(MsgPackStreamReader.ReadResult item)
    {
        Assert.Null(item.State);
        Assert.Null(item.Frame);
        return Assert.IsType<MsgPackStreamReader.NvimResponse>(item.Response);
    }

    /// <summary>A response arrives on its own channel when it decoded whole,
    /// and as a generic frame when a split sent it through the probe path -
    /// either way the msgid must be intact.</summary>
    private static void AssertResponse(MsgPackStreamReader.ReadResult item, long msgId)
    {
        if (item.Response is MsgPackStreamReader.NvimResponse response)
            Assert.Equal((uint)msgId, response.MsgId);
        else
            Assert.Equal(msgId, FrameOf(item)[1]);
    }

    private static readonly object[] Response = { 1L, 7L, null!, new object[] { 3L, 0L, "tick" } };
    private static readonly object[] Notification = { 2L, "vsneo_state", new object[] { "n", 12L, 4L } };

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(4096)]
    public async Task Frames_survive_any_split_and_come_out_one_at_a_time(int chunk)
    {
        var data = Encode(Response).Concat(Encode(Notification)).Concat(Encode(Response)).ToArray();
        var items = await ReadAll(data, chunk);

        Assert.Equal(3, items.Count);
        AssertResponse(items[0], 7);
        var notification = FrameOf(items[1]);
        Assert.Equal("vsneo_state", notification[1]);
        Assert.Equal(new object[] { "n", 12L, 4L }, Assert.IsType<object[]>(notification[2]));
        AssertResponse(items[2], 7);
    }

    [Fact]
    public async Task A_frame_larger_than_the_buffer_grows_it()
    {
        // A big nvim_buf_lines_event (gg=G, a prime echo): far past 8 KiB.
        var lines = Enumerable.Range(0, 2000).Select(i => (object)("line " + i + " héllo")).ToArray();
        var big = new object[] { 2L, "nvim_buf_lines_event", new object[] { 1L, 5L, 0L, -1L, lines, false } };
        var items = await ReadAll(Encode(big).Concat(Encode(Notification)).ToArray(), 1000);

        Assert.Equal(2, items.Count);
        var args = Assert.IsType<object[]>(FrameOf(items[0])[2]);
        var back = Assert.IsType<object[]>(args[4]);
        Assert.Equal(2000, back.Length);
        Assert.Equal("line 1999 héllo", back[1999]);
    }

    [Fact]
    public async Task End_of_stream_is_null_not_an_exception()
    {
        Assert.Empty(await ReadAll(Array.Empty<byte>(), 16));
    }

    [Fact]
    public async Task Redraw_keeps_batches_the_hub_handles_and_drops_the_rest()
    {
        var redraw = new object[]
        {
            2L, "redraw", new object[]
            {
                new object[] { "grid_line", new object[] { 1L, 0L, 0L, new object[] { new object[] { "x" } }, false } },
                new object[] { "msg_showmode", new object[] { new object[] { new object[] { 0L, "-- INSERT --" } } } },
                new object[] { "flush", Array.Empty<object>() },
            },
        };
        var items = await ReadAll(Encode(redraw), 5);

        var batches = Assert.IsType<object[]>(FrameOf(Assert.Single(items))[2]);
        Assert.Equal(3, batches.Length);
        Assert.Empty(Assert.IsType<object[]>(batches[0]));   // grid_line: skipped undecoded
        var kept = Assert.IsType<object[]>(batches[1]);
        Assert.Equal("msg_showmode", kept[0]);
        Assert.Equal(2, kept.Length);
        Assert.Empty(Assert.IsType<object[]>(batches[2]));   // flush: not the hub's
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4096)]
    public async Task Only_redraw_notifications_are_decoded_selectively(int chunk)
    {
        // A response whose method slot happens to read "redraw" is not a
        // notification: it must decode in full - on the fast path (whole
        // buffer) and on the generic one (split) alike.
        var response = new object[] { 1L, 9L, null!, new object[] { new object[] { "grid_line", 1L } } };
        var item = Assert.Single(await ReadAll(Encode(response), chunk));

        object? result;
        if (item.Response is MsgPackStreamReader.NvimResponse fast)
        {
            Assert.Equal(9u, fast.MsgId);
            result = fast.Result;
        }
        else
        {
            result = FrameOf(item)[3];
        }

        var payload = Assert.IsType<object[]>(result);
        Assert.Equal(new object[] { "grid_line", 1L }, Assert.IsType<object[]>(payload[0]));
    }

    [Fact]
    public async Task A_response_carries_its_error_or_result_without_a_frame()
    {
        var ok = new object[] { 1L, 42L, null!, 12345L };
        var failed = new object[] { 1L, 43L, new object[] { 1L, "boom" }, null! };
        var items = await ReadAll(Encode(ok).Concat(Encode(failed)).ToArray(), 4096);

        Assert.Equal(2, items.Count);
        var first = ResponseOf(items[0]);
        Assert.Equal(42u, first.MsgId);
        Assert.Null(first.Error);
        Assert.Equal(12345L, first.Result);

        var second = ResponseOf(items[1]);
        Assert.Equal(43u, second.MsgId);
        Assert.NotNull(second.Error);
        Assert.Null(second.Result);
    }

    /// <summary>The companion's real push: eight args, the fast path's shape.</summary>
    private static readonly object[] State8 =
    {
        2L, "vsneo_state", new object[] { "niI", 123L, 42L, 100L, 10L, 3L, true, true }
    };

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(4096)]
    public async Task An_eight_arg_state_push_takes_the_fast_path(int chunk)
    {
        // Two pushes back to back, then a response: each fast path must consume
        // its own bytes exactly and leave the next frame intact.
        var data = Encode(State8).Concat(Encode(State8)).Concat(Encode(Response)).ToArray();
        using var reader = new MsgPackStreamReader(new ChunkedStream(data, chunk));

        for (int i = 0; i < 2; i++)
        {
            var result = await reader.ReadAsync(CancellationToken.None);
            var push = Assert.IsType<StatePush>(result.State);
            Assert.Null(result.Frame);
            Assert.Equal("niI", push.Mode);
            Assert.Equal(123, push.Line);
            Assert.Equal(42, push.ByteColumn);
            Assert.Equal(100, push.TopLine);
            Assert.Equal(10, push.AnchorLine);
            Assert.Equal(3, push.AnchorColumn);
            Assert.True(push.BlockToEol);
            Assert.True(push.Synthetic);
        }

        AssertResponse(await reader.ReadAsync(CancellationToken.None), 7);
    }

    [Fact]
    public async Task Fast_paths_keep_their_order_across_channels()
    {
        // Response, state push, response in one buffer: three different decode
        // paths, one wire order.
        var items = await ReadAll(
            Encode(Response).Concat(Encode(State8)).Concat(Encode(Response)).ToArray(), 4096);

        Assert.Equal(3, items.Count);
        AssertResponse(items[0], 7);
        Assert.IsType<StatePush>(items[1].State);
        AssertResponse(items[2], 7);
    }

    [Fact]
    public async Task A_state_push_with_other_arg_counts_stays_a_frame()
    {
        // Notification above is vsneo_state with three args - an older
        // companion's shape. It must not trip the fixed-shape fast path.
        var items = await ReadAll(Encode(Notification), 4096);
        var args = Assert.IsType<object[]>(FrameOf(Assert.Single(items))[2]);
        Assert.Equal(3, args.Length);
    }
}
