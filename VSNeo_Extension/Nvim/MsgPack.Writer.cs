using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VSNeo_Extension.Nvim;

// The encoder: frames, values, and the pooled buffers the client rents.

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
        WriteRequestFrameHead(msgId, method);
        WriteValue(args);
    }

    /// <summary>The frame up to the params array; the caller writes params
    /// itself (WriteArrayHeader, WriteInt64, WriteSnapshotLines).</summary>
    public void WriteRequestFrameHead(uint msgId, string method)
    {
        Put(0x94);  // fixarray(4)
        Put(0x00);  // request
        WriteInt64(msgId);

        var token = MethodToken(method);
        Need(token.Length);
        System.Buffer.BlockCopy(token, 0, _buf, _n, token.Length);
        _n += token.Length;
    }

    /// <summary>An array header alone, for callers writing their own args.</summary>
    public void WriteArrayHeader(int count) => WriteHeader(count, 0x90, 0xdc, 0xdd);

    /// <summary>
    /// A whole text snapshot as one msgpack array of lines, encoded without
    /// materializing a string per line - the difference between priming a
    /// 10K-line file with zero line allocations and with ten thousand of
    /// them. One pass per line: encode into scratch first, at which point
    /// the byte count the str header needs is known. The encoder runs
    /// across the chunks, so a surrogate pair split across a chunk boundary
    /// still encodes as one character.
    /// </summary>
    public void WriteSnapshotLines(Microsoft.VisualStudio.Text.ITextSnapshot snapshot)
    {
        int lineCount = snapshot.LineCount;
        WriteArrayHeader(lineCount);

        var chars = _lineChars ??= new char[4096];
        var encoder = _lineEncoder ??= Encoding.UTF8.GetEncoder();

        for (int i = 0; i < lineCount; i++)
        {
            var line = snapshot.GetLineFromLineNumber(i);
            int pos = line.Start.Position, end = line.End.Position;
            if (pos == end)
            {
                Put(0xa0); // fixstr(0): an empty line
                continue;
            }

            // The scratch must hold the line's whole UTF-8 payload; the
            // GetMaxByteCount estimate is a safe upper bound, so every
            // chunk Convert below always fits.
            var scratch = _lineScratch;
            int max = Encoding.UTF8.GetMaxByteCount(end - pos);
            if (scratch == null || scratch.Length < max)
            {
                scratch = _lineScratch = new byte[Math.Max(4096, max)];
            }

            encoder.Reset();
            int byteCount = 0;
            while (pos < end)
            {
                int n = Math.Min(chars.Length, end - pos);
                snapshot.CopyTo(pos, chars, 0, n);
                encoder.Convert(chars, 0, n, scratch, byteCount, scratch.Length - byteCount,
                    false, out _, out int used, out _);
                byteCount += used;
                pos += n;
            }

            encoder.Convert(chars, 0, 0, scratch, byteCount, scratch.Length - byteCount,
                true, out _, out int tail, out _);
            byteCount += tail;

            if (byteCount <= 0x1f)
            {
                Put((byte)(0xa0 | byteCount));
            }
            else if (byteCount <= byte.MaxValue)
            { Put(0xd9); Put((byte)byteCount); }
            else if (byteCount <= ushort.MaxValue)
            { Put(0xda); PutBigEndian((ulong)byteCount, 2); }
            else
            { Put(0xdb); PutBigEndian((ulong)byteCount, 4); }

            Need(byteCount);
            System.Buffer.BlockCopy(scratch, 0, _buf, _n, byteCount);
            _n += byteCount;
        }
    }

    private char[]? _lineChars;
    private byte[]? _lineScratch;
    private Encoder? _lineEncoder;

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
        if (MethodTokens.TryGetValue(method, out var cached))
        {
            return cached;
        }

        int count = Encoding.UTF8.GetByteCount(method);
        int header = count <= 0x1f ? 1 : count <= byte.MaxValue ? 2 : 3;
        var token = new byte[header + count];
        if (header == 1)
        {
            token[0] = (byte)(0xa0 | count);
        }
        else if (header == 2)
        { token[0] = 0xd9; token[1] = (byte)count; }
        else
        { token[0] = 0xda; token[1] = (byte)(count >> 8); token[2] = (byte)count; }

        Encoding.UTF8.GetBytes(method, 0, method.Length, token, header);

        if (MethodTokens.Count < MaxMethodTokens)
        {
            MethodTokens.TryAdd(method, token);
        }

        return token;
    }

    public void WriteValue(object value)
    {
        switch (value)
        {
            case null:
                Put(0xc0);
                return;
            case bool b:
                Put(b ? (byte)0xc3 : (byte)0xc2);
                return;
            case string s:
                WriteString(s);
                return;
            case int i:
                WriteInt64(i);
                return;
            case long l:
                WriteInt64(l);
                return;
            case uint u:
                WriteInt64(u);
                return;
            case short sh:
                WriteInt64(sh);
                return;
            case byte by:
                WriteInt64(by);
                return;
            case double d:
                WriteFloat64(d);
                return;
            case float f:
                WriteFloat64(f);
                return;
            case byte[] bytes:
                WriteBinary(bytes);
                return;

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
                foreach (var item in array)
                {
                    WriteValue(item);
                }

                return;
            }

            case ICollection collection:
            {
                WriteHeader(collection.Count, 0x90, 0xdc, 0xdd);
                foreach (var item in collection)
                {
                    WriteValue(item);
                }

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
            if (v <= 0x7f)
            { Put((byte)v); }
            else if (v <= byte.MaxValue)
            { Put(0xcc); Put((byte)v); }
            else if (v <= ushort.MaxValue)
            { Put(0xcd); PutBigEndian((ulong)v, 2); }
            else if (v <= uint.MaxValue)
            { Put(0xce); PutBigEndian((ulong)v, 4); }
            else
            { Put(0xcf); PutBigEndian((ulong)v, 8); }
        }
        else
        {
            if (v >= -32)
            { Put(unchecked((byte)(sbyte)v)); }
            else if (v >= sbyte.MinValue)
            { Put(0xd0); Put(unchecked((byte)(sbyte)v)); }
            else if (v >= short.MinValue)
            { Put(0xd1); PutBigEndian(unchecked((ulong)v), 2); }
            else if (v >= int.MinValue)
            { Put(0xd2); PutBigEndian(unchecked((ulong)v), 4); }
            else
            { Put(0xd3); PutBigEndian(unchecked((ulong)v), 8); }
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
        var scratch = s_encodeScratch;
        int max = Encoding.UTF8.GetMaxByteCount(s.Length);
        if (scratch == null || scratch.Length < max)
        {
            scratch = s_encodeScratch = new byte[Math.Max(256, max)];
        }

        int count = Encoding.UTF8.GetBytes(s, 0, s.Length, scratch, 0);

        if (count <= 0x1f)
        {
            Put((byte)(0xa0 | count));
        }
        else if (count <= byte.MaxValue)
        { Put(0xd9); Put((byte)count); }
        else if (count <= ushort.MaxValue)
        { Put(0xda); PutBigEndian((ulong)count, 2); }
        else
        { Put(0xdb); PutBigEndian((ulong)count, 4); }

        Need(count);
        System.Buffer.BlockCopy(scratch, 0, _buf, _n, count);
        _n += count;
    }

    // Sends come from several threads (UI for input, the verify timer, the
    // RPC thread), so the scratch is per-thread rather than shared.
    [ThreadStatic]
    private static byte[]? s_encodeScratch;

    private void WriteBinary(byte[] bytes)
    {
        if (bytes.Length <= byte.MaxValue)
        { Put(0xc4); Put((byte)bytes.Length); }
        else if (bytes.Length <= ushort.MaxValue)
        { Put(0xc5); PutBigEndian((ulong)bytes.Length, 2); }
        else
        { Put(0xc6); PutBigEndian((ulong)bytes.Length, 4); }

        Need(bytes.Length);
        System.Buffer.BlockCopy(bytes, 0, _buf, _n, bytes.Length);
        _n += bytes.Length;
    }

    /// <summary>Array and map headers differ only in their format bytes.</summary>
    private void WriteHeader(int count, byte fix, byte wide16, byte wide32)
    {
        if (count <= 0x0f)
        {
            Put((byte)(fix | count));
        }
        else if (count <= ushort.MaxValue)
        { Put(wide16); PutBigEndian((ulong)count, 2); }
        else
        { Put(wide32); PutBigEndian((ulong)count, 4); }
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
        while (_buf.Length - _n < count)
        {
            Array.Resize(ref _buf, _buf.Length * 2);
        }
    }
}
