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

        /// <summary>The window being read from, for span consumers (TryReadStringSpan).</summary>
        public byte[] Buffer => _buf;

        /// <summary>The byte at the read position without consuming it; 0 at the end of the window.</summary>
        public byte PeekByte() => _pos < _end ? _buf[_pos] : (byte)0;

        /// <summary>
        /// Reads only an array header and hands back the element count. Anything
        /// else is a corrupt stream (the same stance TryReadValue takes), not a
        /// short read.
        /// </summary>
        public bool TryReadArrayHeader(out int count)
        {
            count = 0;
            if (_pos >= _end) return false;

            byte b = _buf[_pos++];
            if (b >= 0x90 && b <= 0x9f) { count = b & 0x0f; return true; }
            if (b == 0xdc) return TryReadLength(2, out count);
            if (b == 0xdd) return TryReadLength(4, out count);

            throw new InvalidDataException(
                "Expected msgpack array header, got format byte 0x" + b.ToString("x2") + ".");
        }

        /// <summary>
        /// Advances past one value without materializing it. Same partial-read
        /// contract as TryReadValue: false means retry from the original start
        /// once more bytes have arrived.
        ///
        /// This exists for the redraw stream: with ext_linegrid attached, every
        /// repaint ships the whole grid as cell runs, and the hub consumes only
        /// the cmdline/message/popupmenu events. Decoding the rest was thousands
        /// of boxed objects per repaint, all immediately discarded.
        /// </summary>
        /// <summary>
        /// Consumes [fixarray(3), fixint(2), fixstr(11) "vsneo_state", fixarray(8)]
        /// in one shot. False when the bytes do not match or are not all
        /// present; as with every read here, the caller retries from its
        /// original start, so partial consumption is harmless.
        /// </summary>
        public bool TrySkipStatePrefix()
        {
            // 0x93 = fixarray(3), 0x02 = notification, 0xab = fixstr(11),
            // "vsneo_state", 0x98 = fixarray(8): 15 bytes in all.
            if (_end - _pos < 15) return false;
            if (_buf[_pos] != 0x93 || _buf[_pos + 1] != 0x02 || _buf[_pos + 2] != 0xab
                || _buf[_pos + 14] != 0x98)
                return false;
            for (int i = 0; i < StateMethodBytes.Length; i++)
                if (_buf[_pos + 3 + i] != StateMethodBytes[i]) return false;
            _pos += 15;
            return true;
        }

        private static readonly byte[] StateMethodBytes =
        {
            (byte)'v', (byte)'s', (byte)'n', (byte)'e', (byte)'o', (byte)'_',
            (byte)'s', (byte)'t', (byte)'a', (byte)'t', (byte)'e',
        };

        /// <summary>True for every msgpack int format byte (fixints included).</summary>
        public static bool IsIntToken(byte b) =>
            b <= 0x7f || b >= 0xe0 || (b >= 0xcc && b <= 0xd3);

        /// <summary>
        /// Any msgpack int form, decoded without boxing. The state-push fast
        /// path reads six of these per keystroke. Anything but an int is a
        /// corrupt stream, same as TryReadValue.
        /// </summary>
        public bool TryReadLong(out long value)
        {
            value = 0;
            if (_pos >= _end) return false;

            byte b = _buf[_pos++];
            if (b <= 0x7f) { value = b; return true; }
            if (b >= 0xe0) { value = (sbyte)b; return true; }

            int width;
            bool signed;
            switch (b)
            {
                case 0xcc: width = 1; signed = false; break;
                case 0xcd: width = 2; signed = false; break;
                case 0xce: width = 4; signed = false; break;
                case 0xcf: width = 8; signed = false; break;
                case 0xd0: width = 1; signed = true; break;
                case 0xd1: width = 2; signed = true; break;
                case 0xd2: width = 4; signed = true; break;
                case 0xd3: width = 8; signed = true; break;
                default:
                    throw new InvalidDataException(
                        "Expected msgpack int, got format byte 0x" + b.ToString("x2") + ".");
            }

            if (_end - _pos < width) return false;

            if (signed)
            {
                long v = (sbyte)_buf[_pos];
                for (int i = 1; i < width; i++) v = (v << 8) | _buf[_pos + i];
                value = v;
            }
            else
            {
                ulong u = 0;
                for (int i = 0; i < width; i++) u = (u << 8) | _buf[_pos + i];
                value = unchecked((long)u);
            }

            _pos += width;
            return true;
        }

        /// <summary>0xc2/0xc3; anything else is a corrupt stream.</summary>
        public bool TryReadBool(out bool value)
        {
            value = false;
            if (_pos >= _end) return false;

            byte b = _buf[_pos++];
            if (b == 0xc2) return true;
            if (b == 0xc3) { value = true; return true; }

            throw new InvalidDataException(
                "Expected msgpack bool, got format byte 0x" + b.ToString("x2") + ".");
        }

        /// <summary>
        /// Reads a str value without decoding it: the byte range of its UTF-8
        /// payload is handed back instead. The redraw reader matches batch names
        /// on the raw bytes, so a skipped batch costs no string allocation at
        /// all - with ext_linegrid attached that is most batches of every repaint.
        /// Anything but a str is a corrupt stream, same as TryReadValue.
        /// </summary>
        public bool TryReadStringSpan(out int offset, out int length)
        {
            offset = 0;
            length = 0;
            if (_pos >= _end) return false;

            byte b = _buf[_pos];
            int len;
            if (b >= 0xa0 && b <= 0xbf) { len = b & 0x1f; _pos++; }
            else if (b == 0xd9) { _pos++; if (!TryReadLength(1, out len)) return false; }
            else if (b == 0xda) { _pos++; if (!TryReadLength(2, out len)) return false; }
            else if (b == 0xdb) { _pos++; if (!TryReadLength(4, out len)) return false; }
            else
                throw new InvalidDataException(
                    "Expected msgpack str, got format byte 0x" + b.ToString("x2") + ".");

            if (_end - _pos < len) return false;

            offset = _pos;
            length = len;
            _pos += len;
            return true;
        }

        /// <summary>Decodes a previously captured string span (see TryReadStringSpan).</summary>
        public static string DecodeString(byte[] buf, int offset, int length) =>
            Encoding.UTF8.GetString(buf, offset, length);

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public unsafe bool TrySkipValue()
        {
            // One pinning per top-level skip; the recursive walk below shares
            // the raw pointer, so a value costs a load and a compare - the
            // explicit _pos < _end checks keep the same contract as the
            // managed path, only the array bounds checks are gone.
            fixed (byte* p = _buf)
                return TrySkipValue(p);
        }

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private unsafe bool TrySkipValue(byte* p)
        {
            if (_pos >= _end) return false;

            byte b = p[_pos++];

            if (b <= 0x7f || b >= 0xe0) return true;                       // fixints
            if (b >= 0xa0 && b <= 0xbf) return TrySkip(b & 0x1f);          // fixstr
            if (b >= 0x90 && b <= 0x9f) return TrySkipValues(b & 0x0f, p); // fixarray
            if (b >= 0x80 && b <= 0x8f) return TrySkipMap(b & 0x0f, p);    // fixmap
            return TrySkipValueWide(b, p);
        }

        /// <summary>
        /// The wide-format tail of <see cref="TrySkipValue"/>, split out so the
        /// hot fixint/fixstr path stays small enough to inline into the array
        /// skip loop - which is where nearly every skipped value sits.
        /// </summary>
        private unsafe bool TrySkipValueWide(byte b, byte* p)
        {
            int length;
            switch (b)
            {
                case 0xc0: case 0xc2: case 0xc3: return true;

                case 0xc4: return TryReadLength(1, out length) && TrySkip(length);
                case 0xc5: return TryReadLength(2, out length) && TrySkip(length);
                case 0xc6: return TryReadLength(4, out length) && TrySkip(length);

                // EXT payload is preceded by a one-byte type code.
                case 0xc7: return TryReadLength(1, out length) && TrySkip(length + 1);
                case 0xc8: return TryReadLength(2, out length) && TrySkip(length + 1);
                case 0xc9: return TryReadLength(4, out length) && TrySkip(length + 1);

                case 0xca: return TrySkip(4);
                case 0xcb: return TrySkip(8);

                case 0xcc: case 0xd0: return TrySkip(1);
                case 0xcd: case 0xd1: return TrySkip(2);
                case 0xce: case 0xd2: return TrySkip(4);
                case 0xcf: case 0xd3: return TrySkip(8);

                // fixextN: one type byte plus N payload bytes.
                case 0xd4: return TrySkip(2);
                case 0xd5: return TrySkip(3);
                case 0xd6: return TrySkip(5);
                case 0xd7: return TrySkip(9);
                case 0xd8: return TrySkip(17);

                case 0xd9: return TryReadLength(1, out length) && TrySkip(length);
                case 0xda: return TryReadLength(2, out length) && TrySkip(length);
                case 0xdb: return TryReadLength(4, out length) && TrySkip(length);

                case 0xdc: return TryReadLength(2, out length) && TrySkipValues(length, p);
                case 0xdd: return TryReadLength(4, out length) && TrySkipValues(length, p);

                case 0xde: return TryReadLength(2, out length) && TrySkipMap(length, p);
                case 0xdf: return TryReadLength(4, out length) && TrySkipMap(length, p);

                default:
                    throw new InvalidDataException(
                        "Unknown msgpack format byte 0x" + b.ToString("x2") + ".");
            }
        }

        private bool TrySkip(int count)
        {
            if (_end - _pos < count) return false;
            _pos += count;
            return true;
        }

        private unsafe bool TrySkipValues(int count, byte* p)
        {
            for (int i = 0; i < count; i++)
                if (!TrySkipValue(p)) return false;
            return true;
        }

        private unsafe bool TrySkipMap(int count, byte* p)
        {
            // Keys and values interleave; the doubling guards against a corrupt
            // length wrapping negative before the loop ever runs.
            if (count > int.MaxValue / 2)
                throw new InvalidDataException("msgpack map length " + count + " is implausible.");
            return TrySkipValues(count * 2, p);
        }

        /// <summary>
        /// Boxed constants for the values that dominate nvim's traffic: small
        /// integers (line numbers, columns, attr ids, counts, flags) and the two
        /// booleans. Boxing allocates, and the redraw/state stream boxes several
        /// values per keystroke on the RPC thread - in devenv's heap that is gen2
        /// pressure the UI thread eventually pauses for. Identity is safe to
        /// share: consumers convert, never mutate.
        /// </summary>
        private const int SmallLongLow = -32;
        private const int SmallLongHigh = 1023;
        private static readonly object[] SmallLongs = InitSmallLongs();
        private static readonly object BoxedTrue = true;
        private static readonly object BoxedFalse = false;

        private static object[] InitSmallLongs()
        {
            var values = new object[SmallLongHigh - SmallLongLow + 1];
            for (int i = 0; i < values.Length; i++) values[i] = (long)(i + SmallLongLow);
            return values;
        }

        private static object BoxLong(long v) =>
            v >= SmallLongLow && v <= SmallLongHigh ? SmallLongs[v - SmallLongLow] : (object)v;

        /// <summary>
        /// Reads one value. Returns false when the window does not hold a complete
        /// value, in which case <see cref="Position"/> is meaningless and the caller
        /// must retry from its original start offset once more bytes have arrived.
        /// Partial consumption is therefore harmless: nothing commits until true.
        /// A msgpack nil is a successful read with a null value.
        /// </summary>
        public bool TryReadValue(out object? value)
        {
            value = null;
            if (_pos >= _end) return false;

            byte b = _buf[_pos++];

            if (b <= 0x7f) { value = SmallLongs[b - SmallLongLow]; return true; }   // positive fixint
            if (b >= 0xe0) { value = SmallLongs[(sbyte)b - SmallLongLow]; return true; }   // negative fixint
            if (b >= 0xa0 && b <= 0xbf) return TryReadString(b & 0x1f, out value);
            if (b >= 0x90 && b <= 0x9f) return TryReadArray(b & 0x0f, out value);
            if (b >= 0x80 && b <= 0x8f) return TryReadMap(b & 0x0f, out value);

            int length;
            switch (b)
            {
                case 0xc0: value = null; return true;
                case 0xc2: value = BoxedFalse; return true;
                case 0xc3: value = BoxedTrue; return true;

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

        private bool TryReadUInt(int width, out object? value)
        {
            value = null;
            if (_end - _pos < width) return false;

            ulong v = 0;
            for (int i = 0; i < width; i++) v = (v << 8) | _buf[_pos + i];
            _pos += width;

            // uint64 above long.MaxValue does not occur in nvim's protocol; the
            // unchecked cast keeps the uniform "integers are long" contract.
            value = BoxLong(unchecked((long)v));
            return true;
        }

        private bool TryReadInt(int width, out object? value)
        {
            value = null;
            if (_end - _pos < width) return false;

            long v = (sbyte)_buf[_pos]; // sign-extend from the leading byte
            for (int i = 1; i < width; i++) v = (v << 8) | _buf[_pos + i];
            _pos += width;

            value = BoxLong(v);
            return true;
        }

        private bool TryReadFloat32(out object? value)
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

        private bool TryReadFloat64(out object? value)
        {
            value = null;
            if (_end - _pos < 8) return false;

            long bits = 0;
            for (int i = 0; i < 8; i++) bits = (bits << 8) | _buf[_pos + i];
            _pos += 8;

            value = BitConverter.Int64BitsToDouble(bits);
            return true;
        }

        private bool TryReadString(int length, out object? value)
        {
            value = null;
            if (_end - _pos < length) return false;

            value = Encoding.UTF8.GetString(_buf, _pos, length);
            _pos += length;
            return true;
        }

        private bool TryReadBinary(int length, out object? value)
        {
            value = null;
            if (_end - _pos < length) return false;

            var bytes = new byte[length];
            System.Buffer.BlockCopy(_buf, _pos, bytes, 0, length);
            _pos += length;

            value = bytes;
            return true;
        }

        private bool TryReadArray(int count, out object? value)
        {
            value = null;

            var items = new object?[count];
            for (int i = 0; i < count; i++)
                if (!TryReadValue(out items[i])) return false;

            value = items;
            return true;
        }

        private bool TryReadMap(int count, out object? value)
        {
            value = null;

            var map = new Dictionary<string, object?>(count);
            for (int i = 0; i < count; i++)
            {
                if (!TryReadValue(out var key)) return false;
                if (!TryReadValue(out var item)) return false;
                // AsString tolerates a null at runtime, but its parameter is
                // non-nullable, so the nil-key case is folded in here instead.
                map[key is null ? string.Empty : NvimStateHub.AsString(key) ?? string.Empty] = item;
            }

            value = map;
            return true;
        }

        /// <summary>
        /// EXT is marker, then length (already consumed by the caller for the
        /// variable-width forms), then a one-byte type code, then the payload. The
        /// payload of an nvim handle is itself a msgpack integer.
        /// </summary>
        private bool TryReadExtension(int length, out object? value)
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

        /// <summary>Rewinds for reuse; the buffer is kept. Pooled per send (NvimRpcClient).</summary>
        public void Reset() => _n = 0;

        /// <summary>
        /// [0, msgid, method, params] written directly: no frame array and no
        /// boxed msgid. The per-keystroke request path (apply_spans while
        /// typing) goes through here.
        /// </summary>
        public void WriteRequestFrame(uint msgId, string method, object[] args)
        {
            Put(0x94);  // fixarray(4)
            Put(0x00);  // request
            WriteInt64(msgId);

            var token = MethodToken(method);
            Need(token.Length);
            System.Buffer.BlockCopy(token, 0, _buf, _n, token.Length);
            _n += token.Length;

            WriteValue(args);
        }

        /// <summary>
        /// The header and UTF-8 bytes of a method name are constant, so they
        /// are encoded once: the request path writes one per typed character.
        /// The dictionary is capped; past it a name encodes fresh, exactly as
        /// before, rather than growing without bound.
        /// </summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> MethodTokens
            = new System.Collections.Concurrent.ConcurrentDictionary<string, byte[]>(StringComparer.Ordinal);
        private const int MaxMethodTokens = 64;

        private static byte[] MethodToken(string method)
        {
            if (MethodTokens.TryGetValue(method, out var cached)) return cached;

            int count = Encoding.UTF8.GetByteCount(method);
            int header = count <= 0x1f ? 1 : count <= byte.MaxValue ? 2 : 3;
            var token = new byte[header + count];
            if (header == 1) token[0] = (byte)(0xa0 | count);
            else if (header == 2) { token[0] = 0xd9; token[1] = (byte)count; }
            else { token[0] = 0xda; token[1] = (byte)(count >> 8); token[2] = (byte)count; }
            Encoding.UTF8.GetBytes(method, 0, method.Length, token, header);

            if (MethodTokens.Count < MaxMethodTokens)
                MethodTokens.TryAdd(method, token);
            return token;
        }

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
            // Encode once into scratch and size the header from the result.
            // GetByteCount + GetBytes (the previous shape) walks the string
            // twice, and every outbound line of buffer text goes through here.
            var scratch = _encodeScratch;
            int max = Encoding.UTF8.GetMaxByteCount(s.Length);
            if (scratch == null || scratch.Length < max)
                scratch = _encodeScratch = new byte[Math.Max(256, max)];
            int count = Encoding.UTF8.GetBytes(s, 0, s.Length, scratch, 0);

            if (count <= 0x1f) Put((byte)(0xa0 | count));
            else if (count <= byte.MaxValue) { Put(0xd9); Put((byte)count); }
            else if (count <= ushort.MaxValue) { Put(0xda); PutBigEndian((ulong)count, 2); }
            else { Put(0xdb); PutBigEndian((ulong)count, 4); }

            Need(count);
            System.Buffer.BlockCopy(scratch, 0, _buf, _n, count);
            _n += count;
        }

        // Sends come from several threads (UI for input, the verify timer, the
        // RPC thread), so the scratch is per-thread rather than shared.
        [ThreadStatic]
        private static byte[]? _encodeScratch;

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
        /// <summary>
        /// The outcome of one read: either a fully decoded frame, or a state
        /// push decoded by the fixed-shape fast path (no frame, no args array,
        /// no per-element boxing). A default (null Frame) result with State
        /// null means end of stream.
        /// </summary>
        public readonly struct ReadResult
        {
            public readonly object[]? Frame;
            public readonly StatePush? State;

            public ReadResult(object[] frame) { Frame = frame; State = null; }
            public ReadResult(StatePush state) { Frame = null; State = state; }
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
                if (TryParseFrame(out var result)) return result;

                MakeRoom();
                int read = await _stream
                    .ReadAsync(_buf, _end, _buf.Length - _end, ct)
                    .ConfigureAwait(false);

                if (read == 0) return default; // nvim exited
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
            if (_start >= _end) return false;

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
                    if (_start == _end) _start = _end = 0; // fully drained, rewind
                    result = new ReadResult(push);
                    return true;
                case StateFrameResult.Incomplete:
                    return false;
            }

            if (_probingOnly)
            {
                var probe = new MsgPackReader(_buf, _start, _end);
                if (!probe.TrySkipValue()) return false;
                _probingOnly = false;
            }

            var reader = new MsgPackReader(_buf, _start, _end);
            if (!TryReadFrame(ref reader, out var frame))
            {
                _probingOnly = true;
                return false;
            }

            _start = reader.Position;
            if (_start == _end) _start = _end = 0; // fully drained, rewind to the front

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
            if (!reader.TrySkipStatePrefix()) return StateFrameResult.NotState;

            if (!IsStrToken(reader.PeekByte())) return StateFrameResult.NotState;
            if (!reader.TryReadStringSpan(out int modeOffset, out int modeLength))
                return StateFrameResult.Incomplete;
            if (modeLength > 16) return StateFrameResult.NotState;

            // Field types are peeked before reading: anything odd - an older
            // companion's idea of the args, a nil - is the generic decoder's
            // business (NotState), while a confirmed token that fails its read
            // is a short buffer (Incomplete). Reading the scalars is where a
            // split is most likely: the frame is 45-ish bytes and pipe reads
            // do not split it, but a split is legal.
            long line, col, topLine, anchorLine, anchorCol;
            bool blockToEol, synthetic;
            if (!MsgPackReader.IsIntToken(reader.PeekByte())) return StateFrameResult.NotState;
            if (!reader.TryReadLong(out line)) return StateFrameResult.Incomplete;
            if (!MsgPackReader.IsIntToken(reader.PeekByte())) return StateFrameResult.NotState;
            if (!reader.TryReadLong(out col)) return StateFrameResult.Incomplete;
            if (!MsgPackReader.IsIntToken(reader.PeekByte())) return StateFrameResult.NotState;
            if (!reader.TryReadLong(out topLine)) return StateFrameResult.Incomplete;
            if (!MsgPackReader.IsIntToken(reader.PeekByte())) return StateFrameResult.NotState;
            if (!reader.TryReadLong(out anchorLine)) return StateFrameResult.Incomplete;
            if (!MsgPackReader.IsIntToken(reader.PeekByte())) return StateFrameResult.NotState;
            if (!reader.TryReadLong(out anchorCol)) return StateFrameResult.Incomplete;
            if (!IsBoolToken(reader.PeekByte())) return StateFrameResult.NotState;
            if (!reader.TryReadBool(out blockToEol)) return StateFrameResult.Incomplete;
            if (!IsBoolToken(reader.PeekByte())) return StateFrameResult.NotState;
            if (!reader.TryReadBool(out synthetic)) return StateFrameResult.Incomplete;

            push = new StatePush(
                InternStateMode(reader.Buffer, modeOffset, modeLength),
                unchecked((int)line), unchecked((int)col), unchecked((int)topLine),
                unchecked((int)anchorLine), unchecked((int)anchorCol),
                blockToEol, synthetic);
            return StateFrameResult.Success;
        }

        private static bool IsBoolToken(byte b) => b == 0xc2 || b == 0xc3;

        private string? _lastStateMode;

        private string InternStateMode(byte[] buf, int offset, int length)
        {
            var last = _lastStateMode;
            if (last != null && last.Length == length && MatchName(buf, offset, last))
                return last;

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
            if (!reader.TryReadArrayHeader(out int count)) return false;

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
                    if (!TryReadMethod(ref reader, out items[1])) return false;
                    continue;
                }

                // [2, "redraw", args]: recognized only once the first two elements
                // are in hand. A notification is the only frame whose third
                // element can be a redraw batch list.
                if (i == 2
                    && items[0] is long type && type == 2
                    && items[1] is string method && method == "redraw")
                {
                    if (!TryReadRedrawArgs(ref reader, out items[2])) return false;
                }
                else
                {
                    if (!reader.TryReadValue(out items[i])) return false;
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
            if (!reader.TryReadStringSpan(out int offset, out int length)) return false;

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
                if (buf[offset + i] != (byte)s[i]) return false;
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
            if (!reader.TryReadArrayHeader(out int batchCount)) return false;

            var batches = new object?[batchCount];
            for (int b = 0; b < batchCount; b++)
            {
                if (!reader.TryReadArrayHeader(out int itemCount)) return false;
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
                    if (!reader.TryReadStringSpan(out int nameOffset, out int nameLength)) return false;
                    name = NvimStateHub.MatchHandledRedrawEvent(reader.Buffer, nameOffset, nameLength);
                }
                else
                {
                    if (!reader.TryReadValue(out var nameObj)) return false;
                    var s = nameObj as string;
                    if (s != null && NvimStateHub.IsHandledRedrawEvent(s)) name = s;
                }

                if (name != null)
                {
                    var batch = new object?[itemCount];
                    batch[0] = name;
                    for (int i = 1; i < itemCount; i++)
                        if (!reader.TryReadValue(out batch[i])) return false;
                    batches[b] = batch;
                }
                else
                {
                    for (int i = 1; i < itemCount; i++)
                        if (!reader.TrySkipValue()) return false;
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
