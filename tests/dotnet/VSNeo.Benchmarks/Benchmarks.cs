using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using VSNeo_Extension.Infrastructure;
using VSNeo_Extension.Nvim;

namespace VSNeo.Benchmarks
{
    /// <summary>Serves the same frame bytes forever, so the long-lived
    /// MsgPackStreamReader (one per session in production) never sees EOF.
    /// Reads are frame-aligned: nvim writes one flush per keystroke, so a pipe
    /// read hands over whole frames - modeling that is what makes the
    /// probe-vs-decode accounting honest.</summary>
    internal sealed class CircularFrameStream : Stream
    {
        private readonly byte[] _frame;
        private int _pos;

        public CircularFrameStream(byte[] frame) => _frame = frame;

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = Math.Min(count, _frame.Length - _pos);
            Array.Copy(_frame, _pos, buffer, offset, n);
            _pos = (_pos + n) % _frame.Length;
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Task.FromResult(Read(buffer, offset, count));

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// Decoding the inbound stream. The per-keystroke reality: one vsneo_state
    /// push plus a redraw whose linegrid batches are skipped. The burst reality:
    /// a buffer_lines event carrying a whole file after %s or gg=G.
    /// </summary>
    [MemoryDiagnoser]
    public class MsgPackReadBenchmarks
    {
        private MsgPackStreamReader _stateReader = null!;
        private MsgPackStreamReader _redrawReader = null!;
        private MsgPackStreamReader _linesReader = null!;
        private MsgPackStreamReader _responseReader = null!;

        [GlobalSetup]
        public void Setup()
        {
            // [2, "vsneo_state", [mode, line, byteCol, topLine, anchorLine, anchorCol, blockToEol, synthetic]]
            _stateReader = Reader(Encode(new object[]
            {
                2, "vsneo_state", new object[] { "n", 1234L, 42L, 1200L, -1L, -1L, false, false }
            }));

            // [2, "redraw", [batches]]: twenty linegrid batches (skipped), the
            // showcmd update every operator keystroke ships, and a flush.
            var batches = new List<object>();
            for (int g = 0; g < 20; g++)
            {
                var cells = new object[40];
                for (int c = 0; c < cells.Length; c++)
                    cells[c] = new object[] { "x", 3L, 1L };
                batches.Add(new object[] { "grid_line", new object[] { 1L, (long)g, 0L, cells } });
            }
            batches.Add(new object[] { "msg_showcmd", new object[] { new object[] { new object[] { 0L, "d2" } } } });
            batches.Add(new object[] { "flush", new object[0] });
            _redrawReader = Reader(Encode(new object[] { 2, "redraw", batches.ToArray() }));

            // [2, "nvim_buf_lines_event", [buf, tick, first, last, lines, more]]
            var lines = new object[500];
            for (int i = 0; i < lines.Length; i++)
                lines[i] = "    public static string Format" + i + "(int value, string suffix) { return value + suffix; } // line " + i;
            _linesReader = Reader(Encode(new object[]
            {
                2, "nvim_buf_lines_event", new object[] { 1L, 9999L, 0L, -1L, lines, false }
            }));

            // [1, msgid, nil, changedtick]: the apply_spans reply, one per
            // typed character.
            _responseReader = Reader(Encode(new object[] { 1L, 42L, null!, 12345L }));
        }

        private static MsgPackStreamReader Reader(byte[] frame) =>
            new MsgPackStreamReader(new CircularFrameStream(Repeat(frame)));

        // nvim flushes a keystroke's traffic in one write, so the pipe hands over
        // many frames per read and the loop drains them synchronously. Serving 32
        // per refill makes 31 of 32 ops pure parses - without it every op pays an
        // async read the production loop almost never pays, and the floor hides
        // the parse cost being measured.
        private static byte[] Repeat(byte[] frame, int copies = 32)
        {
            var all = new byte[frame.Length * copies];
            for (int i = 0; i < copies; i++)
                Buffer.BlockCopy(frame, 0, all, i * frame.Length, frame.Length);
            return all;
        }

        private static byte[] Encode(object[] frame)
        {
            var w = new MsgPackWriter();
            w.WriteValue(frame);
            var copy = new byte[w.Length];
            Buffer.BlockCopy(w.Buffer, 0, copy, 0, w.Length);
            return copy;
        }

        // The result is consumed without touching the Response channel on
        // purpose: this source also builds against the pre-fast-path MsgPack.cs
        // (git stash) for same-session before/after runs, where a response
        // decodes as a plain frame.
        [Benchmark]
        public int ReadStateFrame()
        {
            if (!_stateReader.TryRead(out var r))
                r = _stateReader.ReadAsync(CancellationToken.None).GetAwaiter().GetResult();
            return r.State?.Line ?? (r.Frame != null ? r.Frame.Length : -1);
        }

        [Benchmark]
        public int ReadRedrawFrame()
        {
            if (!_redrawReader.TryRead(out var r))
                r = _redrawReader.ReadAsync(CancellationToken.None).GetAwaiter().GetResult();
            return r.State?.Line ?? (r.Frame != null ? r.Frame.Length : -1);
        }

        [Benchmark]
        public int ReadLinesFrame()
        {
            if (!_linesReader.TryRead(out var r))
                r = _linesReader.ReadAsync(CancellationToken.None).GetAwaiter().GetResult();
            return r.State?.Line ?? (r.Frame != null ? r.Frame.Length : -1);
        }

        [Benchmark]
        public int ReadResponseFrame()
        {
            if (!_responseReader.TryRead(out var r))
                r = _responseReader.ReadAsync(CancellationToken.None).GetAwaiter().GetResult();
            return r.State?.Line ?? (r.Frame != null ? r.Frame.Length : -1);
        }
    }

    /// <summary>The hub dispatch cost per notification, args already decoded.</summary>
    [MemoryDiagnoser]
    public class StateHubBenchmarks
    {
        private NvimStateHub _hub = null!;
        private object[] _stateArgs = null!;
        private object[] _redrawArgs = null!;
        private StatePush _push;

        [GlobalSetup]
        public void Setup()
        {
            _hub = new NvimStateHub();
            _stateArgs = new object[] { "n", 1234L, 42L, 1200L, -1L, -1L, false, false };
            _push = new StatePush("n", 1234, 42, 1200, -1, -1, false, false);

            // Realistic per-keystroke redraw while composing an operator:
            // showcmd content plus a few linegrid batches the reader replaced
            // with empty arrays (what the hub sees after selective decoding).
            var batches = new List<object>();
            for (int g = 0; g < 20; g++) batches.Add(Array.Empty<object>());
            batches.Add(new object[]
            {
                "msg_showcmd",
                new object[] { new object[] { new object[] { 0L, "d2" } } }
            });
            batches.Add(Array.Empty<object>());
            _redrawArgs = batches.ToArray();
        }

        [Benchmark]
        public void StatePush() => _hub.OnNotification("vsneo_state", _stateArgs);

        /// <summary>The fast path's entry point: same handling, struct input.</summary>
        [Benchmark]
        public void StatePushStruct() => _hub.OnStatePush(_push);

        [Benchmark]
        public void RedrawShowCmd() => _hub.OnNotification("redraw", _redrawArgs);
    }

    /// <summary>Which-key prefix matching, run on the key path per keystroke.</summary>
    [MemoryDiagnoser]
    public class KeymapBenchmarks
    {
        private NvimStateHub _hub = null!;

        [GlobalSetup]
        public void Setup()
        {
            _hub = new NvimStateHub();
            var rng = new Random(42);
            var items = new object[120];
            for (int i = 0; i < items.Length; i++)
            {
                var lhs = " f" + (char)('a' + rng.Next(26)) + (i % 3 == 0 ? ((char)('a' + rng.Next(26))).ToString() : "");
                items[i] = new object[] { lhs, "mapping " + i };
            }
            _hub.OnNotification("vsneo_keymaps", new object[] { "n", items });
        }

        [Benchmark]
        public IReadOnlyList<KeymapEntry> Children() => _hub.KeymapChildren(VimMode.Normal, " f");

        [Benchmark]
        public bool HasChildren() => _hub.HasKeymapChildren(VimMode.Normal, " f");

        [Benchmark]
        public List<string> Split() => NvimStateHub.SplitKeyTokens("<C-w>f<Space>x");
    }

    /// <summary>UTF-8 byte &lt;-&gt; UTF-16 char conversion, per caret move and per edit.</summary>
    [MemoryDiagnoser]
    public class ColumnMapperBenchmarks
    {
        private string _ascii = null!;
        private string _mixed = null!;

        [GlobalSetup]
        public void Setup()
        {
            _ascii = string.Concat(Enumerable.Repeat("a", 180)) + ";";
            // CJK (3 bytes), accented Latin (2 bytes), emoji (surrogate pair, 4 bytes).
            _mixed = string.Concat(Enumerable.Repeat("漢字é😀ab", 20));
        }

        [Benchmark] public int ByteToCharAscii() => ColumnMapper.ByteToChar(_ascii, 150);
        [Benchmark] public int CharToByteAscii() => ColumnMapper.CharToByte(_ascii, 150);
        [Benchmark] public int ByteToCharMixed() => ColumnMapper.ByteToChar(_mixed, 300);
        [Benchmark] public int CharToByteMixed() => ColumnMapper.CharToByte(_mixed, 150);
    }

    /// <summary>Whole-file priming: 500 lines as a string[] vs direct snapshot encode.</summary>
    [MemoryDiagnoser]
    public class SnapshotWriteBenchmarks
    {
        private string[] _lines = null!;
        private Microsoft.VisualStudio.Text.ITextSnapshot _snapshot = null!;
        private MsgPackWriter _pooled = null!;

        [GlobalSetup]
        public void Setup()
        {
            _lines = new string[500];
            for (int i = 0; i < _lines.Length; i++)
                _lines[i] = "    public static string Format" + i + "(int value, string suffix) { return value + suffix; } // line " + i;
            _snapshot = new VSNeo.Tests.Stubs.StubSnapshot(string.Join("\n", _lines));
            _pooled = new MsgPackWriter();
        }

        [Benchmark]
        public int WriteLinesArray()
        {
            _pooled.Reset();
            _pooled.WriteValue(_lines);
            return _pooled.Length;
        }

        /// <summary>Includes the per-line string materialization the old path paid.</summary>
        [Benchmark]
        public int WriteLinesArrayWithStrings()
        {
            var lines = new string[_lines.Length];
            for (int i = 0; i < lines.Length; i++) lines[i] = string.Copy(_lines[i]);
            _pooled.Reset();
            _pooled.WriteValue(lines);
            return _pooled.Length;
        }

        [Benchmark]
        public int WriteSnapshotLines()
        {
            _pooled.Reset();
            _pooled.WriteSnapshotLines(_snapshot);
            return _pooled.Length;
        }
    }

    /// <summary>Outbound encoding: one nvim_input per swallowed keystroke.</summary>
    [MemoryDiagnoser]
    public class MsgPackWriteBenchmarks
    {
        private object[] _inputFrame = null!;
        private object[] _setTextFrame = null!;
        private object[] _setTextArgs = null!;
        private MsgPackWriter _pooled = null!;

        [GlobalSetup]
        public void Setup()
        {
            _inputFrame = new object[] { 2, "nvim_input", new object[] { "<C-w>" } };
            _setTextArgs = new object[] { 1L, 100L, 5L, 100L, 5L, new object[] { "replacement line of text" } };
            _setTextFrame = new object[] { 0, 42L, "nvim_buf_set_text", _setTextArgs };
            _pooled = new MsgPackWriter();
        }

        [Benchmark]
        public int WriteInputFrame()
        {
            var w = new MsgPackWriter();
            w.WriteValue(_inputFrame);
            return w.Length;
        }

        /// <summary>The pooled shape NvimRpcClient now uses: Reset, reuse, no alloc.</summary>
        [Benchmark]
        public int WriteInputFramePooled()
        {
            _pooled.Reset();
            _pooled.WriteValue(_inputFrame);
            return _pooled.Length;
        }

        [Benchmark]
        public int WriteSetTextFrame()
        {
            var w = new MsgPackWriter();
            w.WriteValue(_setTextFrame);
            return w.Length;
        }

        [Benchmark]
        public int WriteSetTextFramePooled()
        {
            _pooled.Reset();
            _pooled.WriteValue(_setTextFrame);
            return _pooled.Length;
        }

        /// <summary>The production request path: direct frame, cached method token.</summary>
        [Benchmark]
        public int WriteRequestFramePooled()
        {
            _pooled.Reset();
            _pooled.WriteRequestFrame(42u, "nvim_buf_set_text", _setTextArgs);
            return _pooled.Length;
        }
    }
}
