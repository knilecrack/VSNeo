using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VSNeo_Extension.Nvim
{
    /// <summary>
    /// The msgpack subset Neovim's RPC actually uses: nil, bool, int, float, str,
    /// bin, array, map, ext. Hand-rolled on purpose.
    ///
    /// Visual Studio loads its own MessagePack.dll in-process. Shipping another
    /// copy inside the VSIX is the landmine documented in the README: a version
    /// that does not match surfaces as unrelated MEF composition errors that never
    /// once mention MessagePack. Owning ~250 lines is the cheaper side of that
    /// trade, and it takes eight transitive assemblies out of the VSIX with it.
    ///
    /// The other reason is EXT. nvim returns Buffer, Window and Tabpage handles as
    /// EXT values, and <c>MessagePackSerializer.Typeless</c> refuses them outright
    /// - "Failed to deserialize System.Object value" on the first redraw carrying a
    /// win_viewport, which faulted the read loop before the mode cache saw an
    /// event. The value layer was already ours because of that.
    ///
    /// Integers all widen to <see cref="long"/> and strings all decode to
    /// <see cref="string"/>, so callers can Convert.ToInt32 without type-sniffing.
    /// </summary>
    internal struct MsgPackReader
    {
        private readonly byte[] _buf;
        private readonly int _end;
        private int _pos;

        public MsgPackReader(byte[] buffer, int start, int end)
        {
            _buf = buffer;
            _pos = start;
            _end = end;
        }

        /// <summary>One past the last byte consumed by a successful read.</summary>
        public int Position => _pos;

        /// <summary>
        /// Reads one value. Returns false when the window does not hold a complete
        /// value, in which case <see cref="Position"/> is meaningless and the caller
        /// must retry from its original start offset once more bytes have arrived.
        /// Partial consumption is therefore harmless: nothing commits until true.
        /// </summary>
        public bool TryReadValue(out object value)
        {
            value = null;
            if (_pos >= _end) return false;

            byte b = _buf[_pos++];

            if (b <= 0x7f) { value = (long)b; return true; }          // positive fixint
            if (b >= 0xe0) { value = (long)(sbyte)b; return true; }   // negative fixint
            if (b >= 0xa0 && b <= 0xbf) return TryReadString(b & 0x1f, out value);
            if (b >= 0x90 && b <= 0x9f) return TryReadArray(b & 0x0f, out value);
            if (b >= 0x80 && b <= 0x8f) return TryReadMap(b & 0x0f, out value);

            int length;
            switch (b)
            {
                case 0xc0: value = null; return true;
                case 0xc2: value = false; return true;
                case 0xc3: value = true; return true;

                case 0xc4: return TryReadLength(1, out length) && TryReadBinary(length, out value);
                case 0xc5: return TryReadLength(2, out length) && TryReadBinary(length, out value);
                case 0xc6: return TryReadLength(4, out length) && TryReadBinary(length, out value);

                case 0xc7: return TryReadLength(1, out length) && TryReadExtension(length, out value);
                case 0xc8: return TryReadLength(2, out length) && TryReadExtension(length, out value);
                case 0xc9: return TryReadLength(4, out length) && TryReadExtension(length, out value);

                case 0xca: return TryReadFloat32(out value);
                case 0xcb: return TryReadFloat64(out value);

                case 0xcc: return TryReadUInt(1, out value);
                case 0xcd: return TryReadUInt(2, out value);
                case 0xce: return TryReadUInt(4, out value);
                case 0xcf: return TryReadUInt(8, out value);

                case 0xd0: return TryReadInt(1, out value);
                case 0xd1: return TryReadInt(2, out value);
                case 0xd2: return TryReadInt(4, out value);
                case 0xd3: return TryReadInt(8, out value);

                case 0xd4: return TryReadExtension(1, out value);
                case 0xd5: return TryReadExtension(2, out value);
                case 0xd6: return TryReadExtension(4, out value);
                case 0xd7: return TryReadExtension(8, out value);
                case 0xd8: return TryReadExtension(16, out value);

                case 0xd9: return TryReadLength(1, out length) && TryReadString(length, out value);
                case 0xda: return TryReadLength(2, out length) && TryReadString(length, out value);
                case 0xdb: return TryReadLength(4, out length) && TryReadString(length, out value);

                case 0xdc: return TryReadLength(2, out length) && TryReadArray(length, out value);
                case 0xdd: return TryReadLength(4, out length) && TryReadArray(length, out value);

                case 0xde: return TryReadLength(2, out length) && TryReadMap(length, out value);
                case 0xdf: return TryReadLength(4, out length) && TryReadMap(length, out value);

                // 0xc1 is unused by the spec and never valid on the wire.
                default:
                    throw new InvalidDataException(
                        "Unknown msgpack format byte 0x" + b.ToString("x2") + ".");
            }
        }

        /// <summary>
        /// Big-endian unsigned length prefix. A length that cannot fit in an int is
        /// a corrupt stream, not a short read: returning false would park the stream
        /// reader forever waiting for bytes that are never coming.
        /// </summary>
        private bool TryReadLength(int width, out int length)
        {
            length = 0;
            if (_end - _pos < width) return false;

            ulong v = 0;
            for (int i = 0; i < width; i++) v = (v << 8) | _buf[_pos + i];
            _pos += width;

            if (v > int.MaxValue)
                throw new InvalidDataException("msgpack length " + v + " exceeds Int32.MaxValue.");

            length = (int)v;
            return true;
        }

        private bool TryReadUInt(int width, out object value)
        {
            value = null;
            if (_end - _pos < width) return false;

            ulong v = 0;
            for (int i = 0; i < width; i++) v = (v << 8) | _buf[_pos + i];
            _pos += width;

            // uint64 above long.MaxValue does not occur in nvim's protocol; the
            // unchecked cast keeps the uniform "integers are long" contract.
            value = unchecked((long)v);
            return true;
        }

        private bool TryReadInt(int width, out object value)
        {
            value = null;
            if (_end - _pos < width) return false;

            long v = (sbyte)_buf[_pos]; // sign-extend from the leading byte
            for (int i = 1; i < width; i++) v = (v << 8) | _buf[_pos + i];
            _pos += width;

            value = v;
            return true;
        }

        private bool TryReadFloat32(out object value)
        {
            value = null;
            if (_end - _pos < 4) return false;

            // .NET Framework has no Int32BitsToSingle, and msgpack is big-endian.
            // Floats never appear in nvim's RPC, so the allocation here is free.
            var bytes = new byte[4];
            for (int i = 0; i < 4; i++) bytes[i] = _buf[_pos + 3 - i];
            _pos += 4;

            value = (double)BitConverter.ToSingle(bytes, 0);
            return true;
        }

        private bool TryReadFloat64(out object value)
        {
            value = null;
            if (_end - _pos < 8) return false;

            long bits = 0;
            for (int i = 0; i < 8; i++) bits = (bits << 8) | _buf[_pos + i];
            _pos += 8;

            value = BitConverter.Int64BitsToDouble(bits);
            return true;
        }

        private bool TryReadString(int length, out object value)
        {
            value = null;
            if (_end - _pos < length) return false;

            value = Encoding.UTF8.GetString(_buf, _pos, length);
            _pos += length;
            return true;
        }

        private bool TryReadBinary(int length, out object value)
        {
            value = null;
            if (_end - _pos < length) return false;

            var bytes = new byte[length];
            Buffer.BlockCopy(_buf, _pos, bytes, 0, length);
            _pos += length;

            value = bytes;
            return true;
        }

        private bool TryReadArray(int count, out object value)
        {
            value = null;

            var items = new object[count];
            for (int i = 0; i < count; i++)
                if (!TryReadValue(out items[i])) return false;

            value = items;
            return true;
        }

        private bool TryReadMap(int count, out object value)
        {
            value = null;

            var map = new Dictionary<string, object>(count);
            for (int i = 0; i < count; i++)
            {
                if (!TryReadValue(out var key)) return false;
                if (!TryReadValue(out var item)) return false;
                map[NvimStateHub.AsString(key) ?? string.Empty] = item;
            }

            value = map;
            return true;
        }

        /// <summary>
        /// EXT is marker, then length (already consumed by the caller for the
        /// variable-width forms), then a one-byte type code, then the payload. The
        /// payload of an nvim handle is itself a msgpack integer.
        /// </summary>
        private bool TryReadExtension(int length, out object value)
        {
            value = null;
            if (_end - _pos < length + 1) return false;

            sbyte typeCode = (sbyte)_buf[_pos++];

            var payload = new MsgPackReader(_buf, _pos, _pos + length);
            _pos += length;

            long id = 0;
            if (payload.TryReadValue(out var raw) && raw is long parsed) id = parsed;

            value = new NvimHandle(typeCode, id);
            return true;
        }
    }

    /// <summary>
    /// Encodes into a growable array. Writing into a plain array rather than an
    /// <c>IBufferWriter</c> also lets SendAsync hand the buffer straight to the
    /// stream with no intermediate copy.
    /// </summary>
    internal sealed class MsgPackWriter
    {
        private byte[] _buf = new byte[512];
        private int _n;

        public byte[] Buffer => _buf;
        public int Length => _n;

        public void WriteValue(object value)
        {
            switch (value)
            {
                case null: Put(0xc0); return;
                case bool b: Put(b ? (byte)0xc3 : (byte)0xc2); return;
                case string s: WriteString(s); return;
                case int i: WriteInt64(i); return;
                case long l: WriteInt64(l); return;
                case uint u: WriteInt64(u); return;
                case short sh: WriteInt64(sh); return;
                case byte by: WriteInt64(by); return;
                case double d: WriteFloat64(d); return;
                case float f: WriteFloat64(f); return;
                case byte[] bytes: WriteBinary(bytes); return;

                // IDictionary before ICollection: Dictionary<,> satisfies both.
                case IDictionary map:
                {
                    WriteHeader(map.Count, 0x80, 0xde, 0xdf);
                    foreach (DictionaryEntry entry in map)
                    {
                        WriteValue(entry.Key);
                        WriteValue(entry.Value);
                    }
                    return;
                }

                case Array array:
                {
                    WriteHeader(array.Length, 0x90, 0xdc, 0xdd);
                    foreach (var item in array) WriteValue(item);
                    return;
                }

                case ICollection collection:
                {
                    WriteHeader(collection.Count, 0x90, 0xdc, 0xdd);
                    foreach (var item in collection) WriteValue(item);
                    return;
                }

                default:
                    // Loud on purpose. Silently shipping a wrong encoding to nvim
                    // desyncs the buffers, which is the failure we can least debug.
                    throw new NotSupportedException(
                        "No msgpack encoding for " + value.GetType().FullName);
            }
        }

        public void WriteInt64(long v)
        {
            if (v >= 0)
            {
                if (v <= 0x7f) { Put((byte)v); }
                else if (v <= byte.MaxValue) { Put(0xcc); Put((byte)v); }
                else if (v <= ushort.MaxValue) { Put(0xcd); PutBigEndian((ulong)v, 2); }
                else if (v <= uint.MaxValue) { Put(0xce); PutBigEndian((ulong)v, 4); }
                else { Put(0xcf); PutBigEndian((ulong)v, 8); }
            }
            else
            {
                if (v >= -32) { Put(unchecked((byte)(sbyte)v)); }
                else if (v >= sbyte.MinValue) { Put(0xd0); Put(unchecked((byte)(sbyte)v)); }
                else if (v >= short.MinValue) { Put(0xd1); PutBigEndian(unchecked((ulong)v), 2); }
                else if (v >= int.MinValue) { Put(0xd2); PutBigEndian(unchecked((ulong)v), 4); }
                else { Put(0xd3); PutBigEndian(unchecked((ulong)v), 8); }
            }
        }

        private void WriteFloat64(double d)
        {
            Put(0xcb);
            PutBigEndian(unchecked((ulong)BitConverter.DoubleToInt64Bits(d)), 8);
        }

        /// <summary>
        /// str, not bin. nvim treats the two differently: method names and buffer
        /// lines have to arrive as str or the call is rejected.
        /// </summary>
        private void WriteString(string s)
        {
            int count = Encoding.UTF8.GetByteCount(s);

            if (count <= 0x1f) Put((byte)(0xa0 | count));
            else if (count <= byte.MaxValue) { Put(0xd9); Put((byte)count); }
            else if (count <= ushort.MaxValue) { Put(0xda); PutBigEndian((ulong)count, 2); }
            else { Put(0xdb); PutBigEndian((ulong)count, 4); }

            Need(count);
            _n += Encoding.UTF8.GetBytes(s, 0, s.Length, _buf, _n);
        }

        private void WriteBinary(byte[] bytes)
        {
            if (bytes.Length <= byte.MaxValue) { Put(0xc4); Put((byte)bytes.Length); }
            else if (bytes.Length <= ushort.MaxValue) { Put(0xc5); PutBigEndian((ulong)bytes.Length, 2); }
            else { Put(0xc6); PutBigEndian((ulong)bytes.Length, 4); }

            Need(bytes.Length);
            System.Buffer.BlockCopy(bytes, 0, _buf, _n, bytes.Length);
            _n += bytes.Length;
        }

        /// <summary>Array and map headers differ only in their format bytes.</summary>
        private void WriteHeader(int count, byte fix, byte wide16, byte wide32)
        {
            if (count <= 0x0f) Put((byte)(fix | count));
            else if (count <= ushort.MaxValue) { Put(wide16); PutBigEndian((ulong)count, 2); }
            else { Put(wide32); PutBigEndian((ulong)count, 4); }
        }

        private void Put(byte b)
        {
            Need(1);
            _buf[_n++] = b;
        }

        /// <summary>Fills back to front, so the shift walks from the low byte up.</summary>
        private void PutBigEndian(ulong v, int width)
        {
            Need(width);
            for (int i = width - 1; i >= 0; i--)
            {
                _buf[_n + i] = (byte)(v & 0xff);
                v >>= 8;
            }
            _n += width;
        }

        private void Need(int count)
        {
            while (_buf.Length - _n < count) Array.Resize(ref _buf, _buf.Length * 2);
        }
    }

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
        public async Task<object[]> ReadFrameAsync(CancellationToken ct)
        {
            while (true)
            {
                if (TryParseFrame(out var frame)) return frame;

                MakeRoom();
                int read = await _stream
                    .ReadAsync(_buf, _end, _buf.Length - _end, ct)
                    .ConfigureAwait(false);

                if (read == 0) return null; // nvim exited
                _end += read;
            }
        }

        private bool TryParseFrame(out object[] frame)
        {
            frame = null;
            if (_start >= _end) return false;

            var reader = new MsgPackReader(_buf, _start, _end);
            if (!reader.TryReadValue(out var value)) return false;

            _start = reader.Position;
            if (_start == _end) _start = _end = 0; // fully drained, rewind to the front

            frame = value as object[];
            if (frame == null)
                throw new InvalidDataException("Top-level msgpack-rpc value was not an array.");

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
                System.Buffer.BlockCopy(_buf, _start, _buf, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }

            if (_end == _buf.Length) Array.Resize(ref _buf, _buf.Length * 2);
        }

        public void Dispose() { }
    }

    /// <summary>An nvim Buffer (0), Window (1) or Tabpage (2) handle.</summary>
    internal readonly struct NvimHandle
    {
        public readonly sbyte Kind;
        public readonly long Id;

        public NvimHandle(sbyte kind, long id)
        {
            Kind = kind;
            Id = id;
        }

        public override string ToString() => "handle(" + Kind + ":" + Id + ")";
    }
}
