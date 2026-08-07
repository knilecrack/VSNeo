using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.Text;
using VSNeo_Extension.Infrastructure;
using VSNeo_Extension.Nvim;

namespace VSNeo_Extension.Editor
{
    /// <summary>
    /// One-way mirror of a Visual Studio buffer into nvim. Milestone 1 keeps this
    /// one-way on purpose: motions need the text, but nothing writes back yet, so
    /// there is no reconciliation to get wrong.
    ///
    /// Edits go over as spans, not as whole files. Visual Studio already reports
    /// exactly what changed in <c>e.Changes</c>, so a keystroke sends one short
    /// nvim_buf_set_text instead of every line in the document. The reverse
    /// direction has the same shape waiting for it: nvim_buf_attach delivers
    /// on_lines with the changed range, which is what milestone 2 applies back into
    /// VS. Whole-buffer traffic then survives only in PrimeAsync.
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
        private readonly string _filePath;
        private readonly HashSet<long> _selfInflictedTicks = new HashSet<long>();
        private bool _disposed;

        private readonly System.Threading.Timer _verify;
        private System.Threading.Tasks.Task<long> _create;
        private long _handle = -1;

        public BufferMirror(ITextBuffer buffer, NvimSession session, string filePath)
        {
            _buffer = buffer;
            _session = session;
            _filePath = filePath;
            _verify = new System.Threading.Timer(
                _ => Verify(), null,
                System.Threading.Timeout.Infinite,
                System.Threading.Timeout.Infinite);
            _buffer.Changed += OnBufferChanged;
            _session.RemoteBufferChanged += ScheduleVerify;
        }

        /// <summary>
        /// This document's nvim buffer, or -1 until it has been created. Read from
        /// the edit path, which must never wait on the creation round trip: an edit
        /// arriving that early is covered by the priming that follows it.
        /// </summary>
        private long Handle => System.Threading.Volatile.Read(ref _handle);

        /// <summary>
        /// Creates the nvim buffer for this document, once. Every document gets its
        /// own, named after the real file.
        ///
        /// Sharing nvim's buffer 0 across all documents was the deeper problem
        /// underneath several smaller ones: two open files overwrote each other,
        /// every focus change resent an entire file to swap the contents over, and
        /// the jumplist, file marks and filetype had nothing stable to attach to.
        /// A named buffer per document removes all of that, and is what any plugin
        /// needs to work at all.
        /// </summary>
        public System.Threading.Tasks.Task<long> EnsureCreatedAsync() =>
            _create ?? (_create = CreateAsync());

        private async System.Threading.Tasks.Task<long> CreateAsync()
        {
            // listed: true so :ls and :b see it like any other file.
            var created = await _session.RequestAsync("nvim_create_buf", true, false)
                                        .ConfigureAwait(false);

            long handle = created is NvimHandle h ? h.Id : Convert.ToInt64(created);
            if (handle <= 0) throw new InvalidOperationException("nvim_create_buf returned " + created);

            // acwrite routes :w to the BufWriteCmd the session installed, so nvim
            // cannot write this path from under Visual Studio.
            await _session.RequestAsync("nvim_buf_set_option", handle, "buftype", "acwrite")
                          .ConfigureAwait(false);

            if (!string.IsNullOrEmpty(_filePath))
                await _session.RequestAsync("nvim_buf_set_name", handle, _filePath)
                              .ConfigureAwait(false);

            System.Threading.Volatile.Write(ref _handle, handle);

            await PrimeAsync().ConfigureAwait(false);

            // Detection has to run after both the name and the contents are in place;
            // some filetypes are decided by the first line, not the extension.
            await _session.RequestAsync(
                "nvim_exec_lua",
                "vim.api.nvim_buf_call(..., function() vim.cmd('filetype detect') end)",
                new object[] { handle }).ConfigureAwait(false);

            Log.Write("nvim buffer " + handle + " created for " + (_filePath ?? "<unnamed>"));
            return handle;
        }

        /// <summary>
        /// Re-checks the two buffers once editing pauses, and resends the file if
        /// they have diverged.
        ///
        /// Sending spans instead of the whole file made divergence permanent. A span
        /// is only meaningful against the exact text nvim already holds, and in
        /// milestone 1 nvim edits its own copy all the time - every normal-mode
        /// operator does, and none of it comes back to VS. Whole-file replace hid
        /// that by resynchronising on every keystroke. This restores the same
        /// self-healing without giving up the cheap path: spans while you type, one
        /// comparison when you stop.
        ///
        /// Visual Studio is authoritative here, which is what makes operators
        /// no-ops rather than corruption. Milestone 2 inverts this by applying
        /// nvim's edits back, and then this check becomes a safety net instead of
        /// the mechanism.
        /// </summary>
        private async void Verify()
        {
            try
            {
                long buf = Handle;
                if (_disposed || !_session.IsReady || buf < 0) return;

                // Snapshots are immutable, so reading one off the UI thread is safe.
                var mine = _buffer.CurrentSnapshot.Lines.Select(l => l.GetText()).ToArray();

                var raw = await _session.RequestAsync("nvim_buf_get_lines", buf, 0, -1, false)
                                        .ConfigureAwait(false) as object[];
                if (raw == null || _disposed) return;

                var theirs = raw.Select(NvimStateHub.AsString).ToArray();
                if (mine.Length == theirs.Length && mine.SequenceEqual(theirs, StringComparer.Ordinal))
                    return;

                Log.Write("mirror drifted in buffer " + buf + " (VS " + mine.Length
                          + " lines, nvim " + theirs.Length + ") - resending");

                await _session.RequestAsync(
                    "nvim_buf_set_lines", buf, 0, -1, false, mine.Cast<object>().ToArray())
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Write("mirror verify failed", ex);
            }
        }

        /// <summary>
        /// Long enough that it never runs mid-keystroke, short enough that the next
        /// motion after you stop typing is already working against correct text.
        /// </summary>
        private const int VerifyDelayMs = 500;

        private void ScheduleVerify()
        {
            try { _verify.Change(VerifyDelayMs, System.Threading.Timeout.Infinite); }
            catch (ObjectDisposedException) { }
        }

        private async System.Threading.Tasks.Task PrimeAsync()
        {
            long buf = Handle;
            if (buf < 0) return;

            var lines = _buffer.CurrentSnapshot.Lines.Select(l => l.GetText()).ToArray();
            await _session.RequestAsync("nvim_buf_set_lines", buf, 0, -1, false, lines)
                          .ConfigureAwait(false);

            // send_buffer false: we only want to be told that something changed, not
            // handed the contents. Verify decides whether it actually matters.
            await _session.RequestAsync(
                "nvim_buf_attach", buf, false, new Dictionary<string, object>())
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Past this many separate spans, one whole-buffer call is cheaper than the
        /// round trips. Format-document and find-replace-all land here.
        /// </summary>
        private const int MaxSpansPerEdit = 64;

        private void OnBufferChanged(object sender, TextContentChangedEventArgs e)
        {
            if (_disposed || !_session.IsReady) return;
            if (IsEchoOfOurOwnApply(e)) return;
            if (e.Changes.Count == 0) return;

            // An edit landing before the buffer exists needs no span: the priming
            // that follows creation sends the current text in full anyway.
            long buf = Handle;
            if (buf < 0) return;

            if (e.Changes.Count > MaxSpansPerEdit)
            {
                ReplaceAll(buf, e.After);
                return;
            }

            // Reverse order matters. Visual Studio reports every change against the
            // *old* snapshot, but each nvim_buf_set_text shifts everything after it.
            // Applying from the last span backwards leaves the earlier offsets still
            // valid, so none of them have to be recomputed.
            for (int i = e.Changes.Count - 1; i >= 0; i--)
                SendSpan(buf, e.Before, e.Changes[i]);

            ScheduleVerify();
        }

        /// <summary>
        /// Translates one VS change into nvim_buf_set_text. Rows are 0-based and
        /// columns are UTF-8 byte offsets, so every column goes through ColumnMapper.
        /// </summary>
        private void SendSpan(long buf, ITextSnapshot before, ITextChange change)
        {
            var startLine = before.GetLineFromPosition(change.OldPosition);
            var endLine = before.GetLineFromPosition(change.OldEnd);

            int startCol = ColumnMapper.CharToByte(
                startLine.GetText(), change.OldPosition - startLine.Start.Position);
            int endCol = ColumnMapper.CharToByte(
                endLine.GetText(), change.OldEnd - endLine.Start.Position);

            Observe(_session.RequestAsync(
                "nvim_buf_set_text", buf,
                startLine.LineNumber, startCol,
                endLine.LineNumber, endCol,
                SplitLines(change.NewText)));
        }

        /// <summary>
        /// nvim wants the replacement as one entry per line. A pure deletion arrives
        /// as empty text, which splits to a single empty string - exactly the "replace
        /// this span with nothing" that joins the two ends together.
        /// </summary>
        private static object[] SplitLines(string text) =>
            string.IsNullOrEmpty(text)
                ? new object[] { string.Empty }
                : text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None)
                      .Cast<object>()
                      .ToArray();

        private void ReplaceAll(long buf, ITextSnapshot snapshot)
        {
            var lines = snapshot.Lines.Select(l => l.GetText()).ToArray();
            Observe(_session.RequestAsync("nvim_buf_set_lines", buf, 0, -1, false, lines));
        }

        /// <summary>
        /// The mirror can race a focus switch and address a span nvim no longer has.
        /// That is self-correcting on the next prime; leaving the task unobserved is
        /// not, so faults are drained rather than left for the finalizer.
        /// </summary>
        private static void Observe(System.Threading.Tasks.Task task) =>
            task.ContinueWith(
                t => Infrastructure.Log.Write("buffer sync span rejected", t.Exception?.GetBaseException()),
                System.Threading.CancellationToken.None,
                System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted,
                System.Threading.Tasks.TaskScheduler.Default);

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
            _session.RemoteBufferChanged -= ScheduleVerify;
            _verify.Dispose();
        }
    }
}
