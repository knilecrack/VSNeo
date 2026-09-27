using VSNeo_Extension.Nvim;
using Xunit;

namespace VSNeo.Tests;

/// <summary>
/// MsgPackStreamReader frames nvim's byte stream into whole RPC messages. The
/// pipe hands data over in arbitrary pieces, so frames must survive any split,
/// back-to-back frames must come out one at a time, a frame bigger than the
/// buffer must grow it, and redraw batches the hub never reads are dropped
/// without being decoded.
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

    private static async Task<List<object[]>> ReadAll(byte[] data, int chunk)
    {
        var frames = new List<object[]>();
        using var reader = new MsgPackStreamReader(new ChunkedStream(data, chunk));
        while (await reader.ReadFrameAsync(CancellationToken.None) is { } frame)
            frames.Add(frame);
        return frames;
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
        var frames = await ReadAll(data, chunk);

        Assert.Equal(3, frames.Count);
        Assert.Equal(7L, frames[0][1]);
        Assert.Equal("vsneo_state", frames[1][1]);
        Assert.Equal(new object[] { "n", 12L, 4L }, Assert.IsType<object[]>(frames[1][2]));
    }

    [Fact]
    public async Task A_frame_larger_than_the_buffer_grows_it()
    {
        // A big nvim_buf_lines_event (gg=G, a prime echo): far past 8 KiB.
        var lines = Enumerable.Range(0, 2000).Select(i => (object)("line " + i + " héllo")).ToArray();
        var big = new object[] { 2L, "nvim_buf_lines_event", new object[] { 1L, 5L, 0L, -1L, lines, false } };
        var frames = await ReadAll(Encode(big).Concat(Encode(Notification)).ToArray(), 1000);

        Assert.Equal(2, frames.Count);
        var args = Assert.IsType<object[]>(frames[0][2]);
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
        var frames = await ReadAll(Encode(redraw), 5);

        var batches = Assert.IsType<object[]>(Assert.Single(frames)[2]);
        Assert.Equal(3, batches.Length);
        Assert.Empty(Assert.IsType<object[]>(batches[0]));   // grid_line: skipped undecoded
        var kept = Assert.IsType<object[]>(batches[1]);
        Assert.Equal("msg_showmode", kept[0]);
        Assert.Equal(2, kept.Length);
        Assert.Empty(Assert.IsType<object[]>(batches[2]));   // flush: not the hub's
    }

    [Fact]
    public async Task Only_redraw_notifications_are_decoded_selectively()
    {
        // A response whose method slot happens to read "redraw" is not a
        // notification: it must decode in full.
        var response = new object[] { 1L, 9L, null!, new object[] { new object[] { "grid_line", 1L } } };
        var frame = Assert.Single(await ReadAll(Encode(response), 3));
        var result = Assert.IsType<object[]>(frame[3]);
        Assert.Equal(new object[] { "grid_line", 1L }, Assert.IsType<object[]>(result[0]));
    }
}
