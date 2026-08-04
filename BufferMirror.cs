using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.Text;
using VSNeo.Nvim;

namespace VSNeo.Editor
{
    /// <summary>
    /// One-way mirror of a Visual Studio buffer into nvim. Milestone 1 keeps this
    /// one-way on purpose: motions need the text, but nothing writes back yet, so
    /// there is no reconciliation to get wrong.
    ///
    /// When milestone 2 turns on nvim_buf_attach and starts applying nvim's edits
    /// back into VS, suppression must key off changedtick rather than a boolean
    /// flag. A flag survives until the first reentrant edit and then silently
    /// corrupts the buffer.
    /// </summary>
    internal sealed class BufferMirror : IDisposable
    {
        private readonly ITextBuffer _buffer;
        private readonly NvimSession _session;
        private readonly HashSet<long> _selfInflictedTicks = new HashSet<long>();
        private bool _disposed;

        public BufferMirror(ITextBuffer buffer, NvimSession session)
        {
            _buffer = buffer;
            _session = session;
            _buffer.Changed += OnBufferChanged;
        }

        public async System.Threading.Tasks.Task PrimeAsync()
        {
            var lines = _buffer.CurrentSnapshot.Lines.Select(l => l.GetText()).ToArray();
            await _session.RequestAsync("nvim_buf_set_lines", 0, 0, -1, false, lines)
                          .ConfigureAwait(false);
        }

        private void OnBufferChanged(object sender, TextContentChangedEventArgs e)
        {
            if (_disposed || !_session.IsReady) return;
            if (IsEchoOfOurOwnApply(e)) return;

            // Milestone 1 replaces wholesale. It is correct and it is slow on large
            // files. Milestone 2 should translate e.Changes into nvim_buf_set_text
            // spans instead, one call per change, batched per edit.
            var lines = e.After.Lines.Select(l => l.GetText()).ToArray();
            _ = _session.RequestAsync("nvim_buf_set_lines", 0, 0, -1, false, lines);
        }

        internal void RecordSelfInflicted(long changedTick)
        {
            lock (_selfInflictedTicks) _selfInflictedTicks.Add(changedTick);
        }

        private bool IsEchoOfOurOwnApply(TextContentChangedEventArgs e)
        {
            if (e.EditTag is string tag && tag == "VSNeo") return true;
            return false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _buffer.Changed -= OnBufferChanged;
        }
    }
}
