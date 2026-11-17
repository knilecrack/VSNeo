using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace VSNeo_Extension.Nvim;

// Frame parsing off the pipe, with the vsneo_state fast path the read loop relies on.

/// <summary>
/// Frames nvim's stdout into whole values. The stream carries no length prefix,
/// so the only way to know a message is complete is to try to parse it and see.
/// A short read leaves the window untouched and we go back for more bytes.
/// </summary>
internal sealed class MsgPackStreamReader : IDisposable
{
    private readonly Stream _stream;
    private byte[] _buf = new byte[8192];
    private int _start;  // first unconsumed byte
    private int _end;    // one past the last byte read

    public MsgPackStreamReader(Stream stream) => _stream = stream;

    /// <summary>
    /// The next RPC frame, or null once nvim closes the stream. Every top-level
    /// msgpack-rpc message is an array; anything else means the stream is
    /// corrupt, which should fault the read loop rather than be skipped.
    /// </summary>
    /// <summary>
    /// The outcome of one read: a fully decoded frame, a state push decoded
    /// by the fixed-shape fast path (no frame, no args array, no per-element
    /// boxing), or a response decoded by its own fixed-head path (no frame
    /// array). A default result - all three null - means end of stream.
    /// </summary>
    public readonly struct ReadResult
    {
        public readonly object[]? Frame;
        public readonly StatePush? State;
        public readonly NvimResponse? Response;

        public ReadResult(object[] frame) { Frame = frame; State = null; Response = null; }
        public ReadResult(StatePush state) { Frame = null; State = state; Response = null; }
        public ReadResult(NvimResponse response) { Frame = null; State = null; Response = response; }

        /// <summary>All channels empty: the read loop's end-of-stream signal.
        /// Testing the three fields individually is how a response once read
        /// as "nvim exited" - keep them behind one property.</summary>
        public bool IsEmpty => Frame == null && State == null && Response == null;
    }

    /// <summary>
    /// One RPC response, [1, msgid, error, result], without the frame array
    /// the generic decode would materialize. One of these lands per request,
    /// and apply_spans ships a request per typed character.
    /// </summary>
    public readonly struct NvimResponse
    {
        public readonly uint MsgId;
        public readonly object? Error;
        public readonly object? Result;

        public NvimResponse(uint msgId, object? error, object? result)
        {
            MsgId = msgId;
            Error = error;
            Result = result;
        }
    }

    /// <summary>
    /// Non-async twin of <see cref="ReadAsync"/>: when a complete item is
    /// already buffered, hand it over with no Task and no state machine.
    /// Frames arriving in the same pipe read as their predecessor (a
    /// keystroke's redraw and its state push) are drained this way - the
    /// async path runs only when the buffer is empty.
    /// </summary>
    public bool TryRead(out ReadResult result) => TryParseFrame(out result);

    /// <summary>
    /// The next RPC item, or a default result once nvim closes the stream.
    /// Every top-level msgpack-rpc message is an array; anything else means
    /// the stream is corrupt, which should fault the read loop rather than
    /// be skipped. vsneo_state notifications never arrive as frames: the
    /// fast path consumes them and hands back a <see cref="StatePush"/>.
    /// </summary>
    public async Task<ReadResult> ReadAsync(CancellationToken ct)
    {
        while (true)
        {
            if (TryParseFrame(out var result))
            {
                return result;
            }

            MakeRoom();
            int read = await _stream
                .ReadAsync(_buf, _end, _buf.Length - _end, ct)
                .ConfigureAwait(false);

            if (read == 0)
            {
                return default; // nvim exited
            }

            _end += read;
        }
    }

    // Set when a decode attempt hit a short read. From then on a probe runs
    // first on every retry, until it proves the frame complete. The probe is
    // the guard against the quadratic case: a frame larger than one pipe
    // read (a big on_lines event from %s or gg=G, a prime echo) arrives over
    // many reads, and decoding from the start on every one of them
    // re-allocates every string again, on the thread the mode cache is
    // published from. First attempts skip the probe deliberately: nearly
    // every frame lands inside a single read (every keystroke's redraw and
    // state push), and those decode in one walk instead of two. The price
    // is one wasted partial decode per multi-read frame - bounded by the
    // frame size, paid once, off the key path's traffic shape.
    private bool _probingOnly;

    private bool TryParseFrame(out ReadResult result)
    {
        result = default;
        if (_start >= _end)
        {
            return false;
        }

        // One per keystroke and always the same ~45-byte shape: decoded on
        // sight, skipping the frame array, the args array and the per-element
        // boxes the generic path would materialize. Anything that is not
        // exactly this shape - an older companion's shorter args above all -
        // falls through to the generic decode.
        var fast = new MsgPackReader(_buf, _start, _end);
        switch (TryReadStateFrame(ref fast, out var push))
        {
            case StateFrameResult.Success:
                _start = fast.Position;
                if (_start == _end)
                {
                    _start = _end = 0; // fully drained, rewind
                }

                result = new ReadResult(push);
                return true;
            case StateFrameResult.Incomplete:
                return false;
        }

        // [1, msgid, error, result]: the second fixed shape on the wire, one
        // per request - and apply_spans ships a request per typed character.
        // The tail values decode generically; a frame split across reads is
        // left for the probe and the generic decoder, like any other.
        if (!_probingOnly
            && _end - _start >= 2
            && _buf[_start] == 0x94 && _buf[_start + 1] == 0x01)
        {
            var responseReader = new MsgPackReader(_buf, _start + 2, _end);
            if (!TryReadResponseTail(ref responseReader, out var response))
            {
                _probingOnly = true;
                return false;
            }

            _start = responseReader.Position;
            if (_start == _end)
            {
                _start = _end = 0; // fully drained, rewind
            }

            result = new ReadResult(response);
            return true;
        }

        if (_probingOnly)
        {
            var probe = new MsgPackReader(_buf, _start, _end);
            if (!probe.TrySkipValue())
            {
                return false;
            }

            _probingOnly = false;
        }

        var reader = new MsgPackReader(_buf, _start, _end);
        if (!TryReadFrame(ref reader, out var frame))
        {
            _probingOnly = true;
            return false;
        }

        _start = reader.Position;
        if (_start == _end)
        {
            _start = _end = 0; // fully drained, rewind to the front
        }

        // TryReadFrame guarantees a non-null frame on success.
        result = new ReadResult(frame!);
        return true;
    }

    private enum StateFrameResult { NotState, Incomplete, Success }

    /// <summary>
    /// [2, "vsneo_state", [mode, line, col, topLine, anchorLine, anchorCol,
    /// blockToEol, synthetic]] with exactly eight args, decoded with no
    /// allocation beyond the mode string (cached; modes arrive in runs).
    /// The prefix is checked byte for byte, so the common non-match costs
    /// one comparison; a match that turns out not to fit the shape is left
    /// for the generic decoder rather than faulted.
    /// </summary>
    private StateFrameResult TryReadStateFrame(ref MsgPackReader reader, out StatePush push)
    {
        push = default;

        // [fixarray(3), fixint(2), fixstr(11)] + "vsneo_state" + fixarray(8)
        if (!reader.TrySkipStatePrefix())
        {
            return StateFrameResult.NotState;
        }

        if (!IsStrToken(reader.PeekByte()))
        {
            return StateFrameResult.NotState;
        }

        if (!reader.TryReadStringSpan(out int modeOffset, out int modeLength))
        {
            return StateFrameResult.Incomplete;
        }

        if (modeLength > 16)
        {
            return StateFrameResult.NotState;
        }

        // Field types are peeked before reading: anything odd - an older
        // companion's idea of the args, a nil - is the generic decoder's
        // business (NotState), while a confirmed token that fails its read
        // is a short buffer (Incomplete). Reading the scalars is where a
        // split is most likely: the frame is 45-ish bytes and pipe reads
        // do not split it, but a split is legal.
        long line, col, topLine, anchorLine, anchorCol;
        bool blockToEol, synthetic;
        if (!MsgPackReader.IsIntToken(reader.PeekByte()))
        {
            return StateFrameResult.NotState;
        }

        if (!reader.TryReadLong(out line))
        {
            return StateFrameResult.Incomplete;
        }

        if (!MsgPackReader.IsIntToken(reader.PeekByte()))
        {
            return StateFrameResult.NotState;
        }

        if (!reader.TryReadLong(out col))
        {
            return StateFrameResult.Incomplete;
        }

        if (!MsgPackReader.IsIntToken(reader.PeekByte()))
        {
            return StateFrameResult.NotState;
        }

        if (!reader.TryReadLong(out topLine))
        {
            return StateFrameResult.Incomplete;
        }

        if (!MsgPackReader.IsIntToken(reader.PeekByte()))
        {
            return StateFrameResult.NotState;
        }

        if (!reader.TryReadLong(out anchorLine))
        {
            return StateFrameResult.Incomplete;
        }

        if (!MsgPackReader.IsIntToken(reader.PeekByte()))
        {
            return StateFrameResult.NotState;
        }

        if (!reader.TryReadLong(out anchorCol))
        {
            return StateFrameResult.Incomplete;
        }

        if (!IsBoolToken(reader.PeekByte()))
        {
            return StateFrameResult.NotState;
        }

        if (!reader.TryReadBool(out blockToEol))
        {
            return StateFrameResult.Incomplete;
        }

        if (!IsBoolToken(reader.PeekByte()))
        {
            return StateFrameResult.NotState;
        }

        if (!reader.TryReadBool(out synthetic))
        {
            return StateFrameResult.Incomplete;
        }

        push = new StatePush(
            InternStateMode(reader.Buffer, modeOffset, modeLength),
            unchecked((int)line), unchecked((int)col), unchecked((int)topLine),
            unchecked((int)anchorLine), unchecked((int)anchorCol),
            blockToEol, synthetic);
        return StateFrameResult.Success;
    }

    /// <summary>
    /// msgid, error and result of a response whose [fixarray(4), fixint(1)]
    /// head the caller already matched. False only on a short buffer - the
    /// caller then retries from the frame's start once more bytes arrive.
    /// </summary>
    private static bool TryReadResponseTail(ref MsgPackReader reader, out NvimResponse response)
    {
        response = default;
        if (!reader.TryReadLong(out long msgId))
        {
            return false;
        }

        if (!reader.TryReadValue(out var error))
        {
            return false;
        }

        if (!reader.TryReadValue(out var result))
        {
            return false;
        }

        response = new NvimResponse(unchecked((uint)msgId), error, result);
        return true;
    }

    private static bool IsBoolToken(byte b) => b == 0xc2 || b == 0xc3;

    private string? _lastStateMode;

    private string InternStateMode(byte[] buf, int offset, int length)
    {
        var last = _lastStateMode;
        if (last != null && last.Length == length && MatchName(buf, offset, last))
        {
            return last;
        }

        var decoded = MsgPackReader.DecodeString(buf, offset, length);
        _lastStateMode = decoded;
        return decoded;
    }

    /// <summary>
    /// Reads one msgpack-rpc frame, decoding redraw notifications selectively:
    /// the batches whose events the state hub handles are materialized, every
    /// other batch (the linegrid cell runs above all) is skipped without
    /// allocating. All other frames decode fully.
    /// </summary>
    private bool TryReadFrame(ref MsgPackReader reader, out object[]? frame)
    {
        frame = null;
        if (!reader.TryReadArrayHeader(out int count))
        {
            return false;
        }

        if (count > reader.Remaining)
        {
            return false;   // same bound as TryReadArray: no allocating for a corrupt header
        }

        var items = new object?[count];
        for (int i = 0; i < count; i++)
        {
            // [2, method, args]: the method name is span-read and interned
            // against the previous frame's - names arrive in runs (a redraw
            // burst, then state pushes), so one byte-compare replaces nearly
            // every string decode and its allocation.
            if (i == 1
                && items[0] is long notification && notification == 2
                && IsStrToken(reader.PeekByte()))
            {
                if (!TryReadMethod(ref reader, out items[1]))
                {
                    return false;
                }

                continue;
            }

            // [2, "redraw", args]: recognized only once the first two elements
            // are in hand. A notification is the only frame whose third
            // element can be a redraw batch list.
            if (i == 2
                && items[0] is long type && type == 2
                && items[1] is string method && method == "redraw")
            {
                if (!TryReadRedrawArgs(ref reader, out items[2]))
                {
                    return false;
                }
            }
            else
            {
                if (!reader.TryReadValue(out items[i]))
                {
                    return false;
                }
            }
        }

        // Null-forgiving on the conversion: TryReadValue's object? element
        // type meets Dispatch's object[] frame contract here, as it did when
        // the whole frame decoded through TryReadValue.
        frame = items!;
        return true;
    }

    private static bool IsStrToken(byte b) =>
        (b >= 0xa0 && b <= 0xbf) || (b >= 0xd9 && b <= 0xdb);

    private string? _lastMethod;

    private bool TryReadMethod(ref MsgPackReader reader, out object? value)
    {
        value = null;
        if (!reader.TryReadStringSpan(out int offset, out int length))
        {
            return false;
        }

        var buf = reader.Buffer;
        var last = _lastMethod;
        if (last != null && last.Length == length && MatchName(buf, offset, last))
        {
            value = last;
        }
        else
        {
            var decoded = MsgPackReader.DecodeString(buf, offset, length);
            _lastMethod = decoded;
            value = decoded;
        }

        return true;
    }

    private static bool MatchName(byte[] buf, int offset, string s)
    {
        // nvim's method names are pure ASCII; a non-ASCII name simply never
        // matches the cache and decodes fresh every time.
        for (int i = 0; i < s.Length; i++)
        {
            if (buf[offset + i] != (byte)s[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Redraw args are batches of [event_name, event, event, ...]. Batches the
    /// hub never handles are replaced with an empty array, which its dispatch
    /// loop already skips. The name is matched on the raw bytes, so a skipped
    /// batch - nearly all of them, with ext_linegrid attached - decodes no
    /// string and allocates nothing; a handled one gets the canonical name
    /// constant back, allocating nothing either.
    /// </summary>
    private static bool TryReadRedrawArgs(ref MsgPackReader reader, out object? value)
    {
        value = null;
        if (!reader.TryReadArrayHeader(out int batchCount))
        {
            return false;
        }

        if (batchCount > reader.Remaining)
        {
            return false;
        }

        var batches = new object?[batchCount];
        for (int b = 0; b < batchCount; b++)
        {
            if (!reader.TryReadArrayHeader(out int itemCount))
            {
                return false;
            }

            if (itemCount > reader.Remaining)
            {
                return false;
            }

            if (itemCount == 0)
            {
                batches[b] = Array.Empty<object>();
                continue;
            }

            // Batch names are str tokens on any healthy stream; a non-str
            // name falls back to the generic read so odd payloads keep the
            // old skip-the-batch behavior instead of faulting the loop.
            string? name = null;
            byte nb = reader.PeekByte();
            if ((nb >= 0xa0 && nb <= 0xbf) || nb == 0xd9 || nb == 0xda || nb == 0xdb)
            {
                if (!reader.TryReadStringSpan(out int nameOffset, out int nameLength))
                {
                    return false;
                }

                name = NvimStateHub.MatchHandledRedrawEvent(reader.Buffer, nameOffset, nameLength);
            }
            else
            {
                if (!reader.TryReadValue(out var nameObj))
                {
                    return false;
                }

                var s = nameObj as string;
                if (s != null && NvimStateHub.IsHandledRedrawEvent(s))
                {
                    name = s;
                }
            }

            if (name != null)
            {
                var batch = new object?[itemCount];
                batch[0] = name;
                for (int i = 1; i < itemCount; i++)
                {
                    if (!reader.TryReadValue(out batch[i]))
                    {
                        return false;
                    }
                }

                batches[b] = batch;
            }
            else
            {
                for (int i = 1; i < itemCount; i++)
                {
                    if (!reader.TrySkipValue())
                    {
                        return false;
                    }
                }

                batches[b] = Array.Empty<object>();
            }
        }

        value = batches;
        return true;
    }

    /// <summary>
    /// Compact first, grow only if compaction did not help. Growth is therefore
    /// bounded by the largest single message rather than by total traffic.
    /// </summary>
    private void MakeRoom()
    {
        if (_start > 0)
        {
            Buffer.BlockCopy(_buf, _start, _buf, 0, _end - _start);
            _end -= _start;
            _start = 0;
        }

        if (_end == _buf.Length)
        {
            Array.Resize(ref _buf, _buf.Length * 2);
        }
    }

    public void Dispose() { }
}
