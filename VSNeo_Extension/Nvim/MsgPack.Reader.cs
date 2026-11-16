using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VSNeo_Extension.Nvim;

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
    public int Remaining => _end - _pos;

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
        if (_pos >= _end)
        {
            return false;
        }

        byte b = _buf[_pos++];
        if (b >= 0x90 && b <= 0x9f)
        { count = b & 0x0f; return true; }

        if (b == 0xdc)
        {
            return TryReadLength(2, out count);
        }

        if (b == 0xdd)
        {
            return TryReadLength(4, out count);
        }

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
        if (_end - _pos < 15)
        {
            return false;
        }

        if (_buf[_pos] != 0x93 || _buf[_pos + 1] != 0x02 || _buf[_pos + 2] != 0xab
            || _buf[_pos + 14] != 0x98)
        {
            return false;
        }

        for (int i = 0; i < StateMethodBytes.Length; i++)
        {
            if (_buf[_pos + 3 + i] != StateMethodBytes[i])
            {
                return false;
            }
        }

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
        if (_pos >= _end)
        {
            return false;
        }

        byte b = _buf[_pos++];
        if (b <= 0x7f)
        { value = b; return true; }

        if (b >= 0xe0)
        { value = (sbyte)b; return true; }

        int width;
        bool signed;
        switch (b)
        {
            case 0xcc:
                width = 1;
                signed = false;
                break;
            case 0xcd:
                width = 2;
                signed = false;
                break;
            case 0xce:
                width = 4;
                signed = false;
                break;
            case 0xcf:
                width = 8;
                signed = false;
                break;
            case 0xd0:
                width = 1;
                signed = true;
                break;
            case 0xd1:
                width = 2;
                signed = true;
                break;
            case 0xd2:
                width = 4;
                signed = true;
                break;
            case 0xd3:
                width = 8;
                signed = true;
                break;
            default:
                throw new InvalidDataException(
                    "Expected msgpack int, got format byte 0x" + b.ToString("x2") + ".");
        }

        if (_end - _pos < width)
        {
            return false;
        }

        if (signed)
        {
            long v = (sbyte)_buf[_pos];
            for (int i = 1; i < width; i++)
            {
                v = (v << 8) | _buf[_pos + i];
            }

            value = v;
        }
        else
        {
            ulong u = 0;
            for (int i = 0; i < width; i++)
            {
                u = (u << 8) | _buf[_pos + i];
            }

            value = unchecked((long)u);
        }

        _pos += width;
        return true;
    }

    /// <summary>0xc2/0xc3; anything else is a corrupt stream.</summary>
    public bool TryReadBool(out bool value)
    {
        value = false;
        if (_pos >= _end)
        {
            return false;
        }

        byte b = _buf[_pos++];
        if (b == 0xc2)
        {
            return true;
        }

        if (b == 0xc3)
        { value = true; return true; }

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
        if (_pos >= _end)
        {
            return false;
        }

        byte b = _buf[_pos];
        int len;
        if (b >= 0xa0 && b <= 0xbf)
        { len = b & 0x1f; _pos++; }
        else if (b == 0xd9)
        {
            _pos++;
            if (!TryReadLength(1, out len))
            {
                return false;
            }
        }
        else if (b == 0xda)
        {
            _pos++;
            if (!TryReadLength(2, out len))
            {
                return false;
            }
        }
        else if (b == 0xdb)
        {
            _pos++;
            if (!TryReadLength(4, out len))
            {
                return false;
            }
        }
        else
        {
            throw new InvalidDataException(
                "Expected msgpack str, got format byte 0x" + b.ToString("x2") + ".");
        }

        if (_end - _pos < len)
        {
            return false;
        }

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
        {
            return TrySkipValue(p);
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private unsafe bool TrySkipValue(byte* p)
    {
        if (_pos >= _end)
        {
            return false;
        }

        byte b = p[_pos++];

        if (b <= 0x7f || b >= 0xe0)
        {
            return true;                       // fixints
        }

        if (b >= 0xa0 && b <= 0xbf)
        {
            return TrySkip(b & 0x1f);          // fixstr
        }

        if (b >= 0x90 && b <= 0x9f)
        {
            return TrySkipValues(b & 0x0f, p); // fixarray
        }

        if (b >= 0x80 && b <= 0x8f)
        {
            return TrySkipMap(b & 0x0f, p);    // fixmap
        }

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
            case 0xc0:
            case 0xc2:
            case 0xc3:
                return true;

            case 0xc4:
                return TryReadLength(1, out length) && TrySkip(length);
            case 0xc5:
                return TryReadLength(2, out length) && TrySkip(length);
            case 0xc6:
                return TryReadLength(4, out length) && TrySkip(length);

            // EXT payload is preceded by a one-byte type code. Not
            // TrySkip(length + 1): a length of int.MaxValue on a corrupt
            // stream overflowed that to int.MinValue, which passed the bounds
            // check and sent _pos hugely negative - the next unsafe read
            // then dereferenced before the pinned buffer.
            case 0xc7:
                return TryReadLength(1, out length) && TrySkipExt(length);
            case 0xc8:
                return TryReadLength(2, out length) && TrySkipExt(length);
            case 0xc9:
                return TryReadLength(4, out length) && TrySkipExt(length);

            case 0xca:
                return TrySkip(4);
            case 0xcb:
                return TrySkip(8);

            case 0xcc:
            case 0xd0:
                return TrySkip(1);
            case 0xcd:
            case 0xd1:
                return TrySkip(2);
            case 0xce:
            case 0xd2:
                return TrySkip(4);
            case 0xcf:
            case 0xd3:
                return TrySkip(8);

            // fixextN: one type byte plus N payload bytes.
            case 0xd4:
                return TrySkip(2);
            case 0xd5:
                return TrySkip(3);
            case 0xd6:
                return TrySkip(5);
            case 0xd7:
                return TrySkip(9);
            case 0xd8:
                return TrySkip(17);

            case 0xd9:
                return TryReadLength(1, out length) && TrySkip(length);
            case 0xda:
                return TryReadLength(2, out length) && TrySkip(length);
            case 0xdb:
                return TryReadLength(4, out length) && TrySkip(length);

            case 0xdc:
                return TryReadLength(2, out length) && TrySkipValues(length, p);
            case 0xdd:
                return TryReadLength(4, out length) && TrySkipValues(length, p);

            case 0xde:
                return TryReadLength(2, out length) && TrySkipMap(length, p);
            case 0xdf:
                return TryReadLength(4, out length) && TrySkipMap(length, p);

            default:
                throw new InvalidDataException(
                    "Unknown msgpack format byte 0x" + b.ToString("x2") + ".");
        }
    }

    private bool TrySkip(int count)
    {
        if (_end - _pos < count)
        {
            return false;
        }

        _pos += count;
        return true;
    }

    /// <summary>Type byte plus payload, with the sum taken in long so an
    /// absurd length reads as "not yet" rather than as a negative skip.</summary>
    private bool TrySkipExt(int length)
    {
        if ((long)_end - _pos < (long)length + 1)
        {
            return false;
        }

        _pos += length + 1;
        return true;
    }

    private unsafe bool TrySkipValues(int count, byte* p)
    {
        for (int i = 0; i < count; i++)
        {
            if (!TrySkipValue(p))
            {
                return false;
            }
        }

        return true;
    }

    private unsafe bool TrySkipMap(int count, byte* p)
    {
        // Keys and values interleave; the doubling guards against a corrupt
        // length wrapping negative before the loop ever runs.
        if (count > int.MaxValue / 2)
        {
            throw new InvalidDataException("msgpack map length " + count + " is implausible.");
        }

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
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = (long)(i + SmallLongLow);
        }

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
        if (_pos >= _end)
        {
            return false;
        }

        byte b = _buf[_pos++];

        if (b <= 0x7f)
        { value = SmallLongs[b - SmallLongLow]; return true; }   // positive fixint

        if (b >= 0xe0)
        { value = SmallLongs[(sbyte)b - SmallLongLow]; return true; }   // negative fixint

        if (b >= 0xa0 && b <= 0xbf)
        {
            return TryReadString(b & 0x1f, out value);
        }

        if (b >= 0x90 && b <= 0x9f)
        {
            return TryReadArray(b & 0x0f, out value);
        }

        if (b >= 0x80 && b <= 0x8f)
        {
            return TryReadMap(b & 0x0f, out value);
        }

        int length;
        switch (b)
        {
            case 0xc0:
                value = null;
                return true;
            case 0xc2:
                value = BoxedFalse;
                return true;
            case 0xc3:
                value = BoxedTrue;
                return true;

            case 0xc4:
                return TryReadLength(1, out length) && TryReadBinary(length, out value);
            case 0xc5:
                return TryReadLength(2, out length) && TryReadBinary(length, out value);
            case 0xc6:
                return TryReadLength(4, out length) && TryReadBinary(length, out value);

            case 0xc7:
                return TryReadLength(1, out length) && TryReadExtension(length, out value);
            case 0xc8:
                return TryReadLength(2, out length) && TryReadExtension(length, out value);
            case 0xc9:
                return TryReadLength(4, out length) && TryReadExtension(length, out value);

            case 0xca:
                return TryReadFloat32(out value);
            case 0xcb:
                return TryReadFloat64(out value);

            case 0xcc:
                return TryReadUInt(1, out value);
            case 0xcd:
                return TryReadUInt(2, out value);
            case 0xce:
                return TryReadUInt(4, out value);
            case 0xcf:
                return TryReadUInt(8, out value);

            case 0xd0:
                return TryReadInt(1, out value);
            case 0xd1:
                return TryReadInt(2, out value);
            case 0xd2:
                return TryReadInt(4, out value);
            case 0xd3:
                return TryReadInt(8, out value);

            case 0xd4:
                return TryReadExtension(1, out value);
            case 0xd5:
                return TryReadExtension(2, out value);
            case 0xd6:
                return TryReadExtension(4, out value);
            case 0xd7:
                return TryReadExtension(8, out value);
            case 0xd8:
                return TryReadExtension(16, out value);

            case 0xd9:
                return TryReadLength(1, out length) && TryReadString(length, out value);
            case 0xda:
                return TryReadLength(2, out length) && TryReadString(length, out value);
            case 0xdb:
                return TryReadLength(4, out length) && TryReadString(length, out value);

            case 0xdc:
                return TryReadLength(2, out length) && TryReadArray(length, out value);
            case 0xdd:
                return TryReadLength(4, out length) && TryReadArray(length, out value);

            case 0xde:
                return TryReadLength(2, out length) && TryReadMap(length, out value);
            case 0xdf:
                return TryReadLength(4, out length) && TryReadMap(length, out value);

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
        if (_end - _pos < width)
        {
            return false;
        }

        ulong v = 0;
        for (int i = 0; i < width; i++)
        {
            v = (v << 8) | _buf[_pos + i];
        }

        _pos += width;

        if (v > int.MaxValue)
        {
            throw new InvalidDataException("msgpack length " + v + " exceeds Int32.MaxValue.");
        }

        length = (int)v;
        return true;
    }

    private bool TryReadUInt(int width, out object? value)
    {
        value = null;
        if (_end - _pos < width)
        {
            return false;
        }

        ulong v = 0;
        for (int i = 0; i < width; i++)
        {
            v = (v << 8) | _buf[_pos + i];
        }

        _pos += width;

        // uint64 above long.MaxValue does not occur in nvim's protocol; the
        // unchecked cast keeps the uniform "integers are long" contract.
        value = BoxLong(unchecked((long)v));
        return true;
    }

    private bool TryReadInt(int width, out object? value)
    {
        value = null;
        if (_end - _pos < width)
        {
            return false;
        }

        long v = (sbyte)_buf[_pos]; // sign-extend from the leading byte
        for (int i = 1; i < width; i++)
        {
            v = (v << 8) | _buf[_pos + i];
        }

        _pos += width;

        value = BoxLong(v);
        return true;
    }

    private bool TryReadFloat32(out object? value)
    {
        value = null;
        if (_end - _pos < 4)
        {
            return false;
        }

        // .NET Framework has no Int32BitsToSingle, and msgpack is big-endian.
        // Floats never appear in nvim's RPC, so the allocation here is free.
        var bytes = new byte[4];
        for (int i = 0; i < 4; i++)
        {
            bytes[i] = _buf[_pos + 3 - i];
        }

        _pos += 4;

        value = (double)BitConverter.ToSingle(bytes, 0);
        return true;
    }

    private bool TryReadFloat64(out object? value)
    {
        value = null;
        if (_end - _pos < 8)
        {
            return false;
        }

        long bits = 0;
        for (int i = 0; i < 8; i++)
        {
            bits = (bits << 8) | _buf[_pos + i];
        }

        _pos += 8;

        value = BitConverter.Int64BitsToDouble(bits);
        return true;
    }

    private bool TryReadString(int length, out object? value)
    {
        value = null;
        if (_end - _pos < length)
        {
            return false;
        }

        value = Encoding.UTF8.GetString(_buf, _pos, length);
        _pos += length;
        return true;
    }

    private bool TryReadBinary(int length, out object? value)
    {
        value = null;
        if (_end - _pos < length)
        {
            return false;
        }

        var bytes = new byte[length];
        System.Buffer.BlockCopy(_buf, _pos, bytes, 0, length);
        _pos += length;

        value = bytes;
        return true;
    }

    private bool TryReadArray(int count, out object? value)
    {
        value = null;

        // Every element is at least one byte, so a count beyond the bytes on
        // hand cannot be a complete frame yet - and allocating for it first
        // turned a corrupt array32 header into an OutOfMemoryException.
        if (count > _end - _pos)
        {
            return false;
        }

        var items = new object?[count];
        for (int i = 0; i < count; i++)
        {
            if (!TryReadValue(out items[i]))
            {
                return false;
            }
        }

        value = items;
        return true;
    }

    private bool TryReadMap(int count, out object? value)
    {
        value = null;

        // Same rule as TryReadArray: a key and a value are two bytes at least.
        if (count > (_end - _pos) / 2)
        {
            return false;
        }

        var map = new Dictionary<string, object?>(count);
        for (int i = 0; i < count; i++)
        {
            if (!TryReadValue(out var key))
            {
                return false;
            }

            if (!TryReadValue(out var item))
            {
                return false;
            }
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
        // In long: length + 1 overflows for int.MaxValue (see TrySkipExt).
        if ((long)_end - _pos < (long)length + 1)
        {
            return false;
        }

        sbyte typeCode = (sbyte)_buf[_pos++];

        var payload = new MsgPackReader(_buf, _pos, _pos + length);
        _pos += length;

        long id = 0;
        if (payload.TryReadValue(out var raw) && raw is long parsed)
        {
            id = parsed;
        }

        value = new NvimHandle(typeCode, id);
        return true;
    }
}
