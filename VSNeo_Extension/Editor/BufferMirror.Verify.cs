using System;
using System.Threading;
using Microsoft.VisualStudio.Text;
using VSNeo_Extension.Infrastructure;
using VSNeo_Extension.Nvim;

namespace VSNeo_Extension.Editor;

// Drift repair: compare the two buffers once editing pauses and resend VS text.
internal sealed partial class BufferMirror
{
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
    private int _verifying;

    /// <summary>
    /// nvim's side of some buffer changed. Only this document's buffer is
    /// this mirror's business; an event that names none (-1) is taken as
    /// possibly ours.
    /// </summary>
    private void OnRemoteBufferChanged(long buf)
    {
        if (buf < 0 || buf == Handle)
        {
            ScheduleVerify();
        }
    }

    // The last state both sides were seen to agree on: Visual Studio's
    // snapshot version and nvim's changedtick, for this nvim buffer (a
    // changedtick means nothing across buffers, and adoption or a
    // re-prime can change which one this mirror writes). While neither
    // has moved, the buffers are still equal and a pass needs no hashing
    // on either side. -1: no agreement known.
    private long _agreedBuffer = -1;
    private int _agreedVersion = -1;
    private long _agreedTick = -1;

#pragma warning disable VSTHRD100
    private async void Verify()
    {
        // async void on a timer: a slow round trip (busy nvim, big file) could
        // otherwise overlap the next scheduled pass, and two concurrent
        // whole-buffer fetches interleave their drift counters and resends.
        if (Interlocked.CompareExchange(ref _verifying, 1, 0) == 1)
        {
            return;
        }

        try
        {
            long buf = Handle;
            if (_disposed || !_session.IsReady || buf < 0)
            {
                return;
            }

            // Snapshots are immutable, so reading one off the UI thread is safe.
            var snapshot = _buffer.CurrentSnapshot;
            int version = snapshot.Version.VersionNumber;

            // Digest compare, not nvim_buf_get_lines(0, -1): the settled case
            // is nearly every pass, and it used to ship the whole file back
            // over the pipe on each editing pause - every line msgpack-decoded
            // into a second managed array, many times per minute on large
            // files. The hash answers the same question in a 64-byte payload.
            //
            // When Visual Studio's side has not moved since the two last
            // agreed, nvim is handed the tick of that agreement: if its side
            // has not moved either, it answers without hashing, and neither
            // side reads the file at all.
            long knownTick = buf == _agreedBuffer && version == _agreedVersion ? _agreedTick : -1;
            var raw = await _session.RequestAsync(
                "nvim_exec_lua", "return vsneo.buffer_hash(...)", new object[] { buf, knownTick })
                                    .ConfigureAwait(false) as object[];
            if (raw == null || raw.Length < 2 || _disposed)
            {
                return;
            }

            var theirsHash = NvimStateHub.AsString(raw[0]);
            int theirLines = Convert.ToInt32(raw[1]);
            long theirTick = raw.Length > 2 ? ToLong(raw[2]) : -1;

            bool unchanged = knownTick >= 0 && string.IsNullOrEmpty(theirsHash);
            if (unchanged
                || (snapshot.LineCount == theirLines
                    && !string.IsNullOrEmpty(theirsHash)
                    && string.Equals(theirsHash, HashSnapshot(snapshot), StringComparison.OrdinalIgnoreCase)))
            {
                _agreedBuffer = theirTick >= 0 ? buf : -1;
                _agreedVersion = version;
                _agreedTick = theirTick;

                // Settled and identical, so nothing can still be in flight. An
                // echo counted but never delivered would otherwise persist for the
                // whole session and swallow a real edit later - which is precisely
                // how the buffers came apart, and the sort of accounting slip that
                // should cost one edit rather than every edit after it.
                // A refusal heals the same way, so converging forgives it too:
                // three isolated ones over a session must not stop the mirror.
                Volatile.Write(ref _consecutiveDrifts, 0);
                Volatile.Write(ref _refusals, 0);
                return;
            }

            _agreedBuffer = -1;
            _agreedVersion = -1;
            _agreedTick = -1;

            // The comparison above was against the snapshot read before the
            // round trip. If Visual Studio has moved on since, the mismatch
            // may be nothing but that edit - its span is already on its way
            // to nvim - and resending the old snapshot would put nvim *behind*
            // the editor: a keystroke typed during the round trip wiped, and
            // an operator run in that window built from stale lines. Not a
            // drift observation, so it neither counts toward the trip nor
            // grows the backoff; re-check from fresh state instead.
            int now = _buffer.CurrentSnapshot.Version.VersionNumber;
            if (_disposed || now != version)
            {
                if (!_disposed)
                {
                    Log.Write("VS edited buffer " + buf + " during verify (v" + version + " -> v" + now
                              + ") - re-checking instead of resending a stale snapshot");
                }

                ScheduleVerify();
                return;
            }

            // Only the drift path needs to resend, and it resends from the
            // snapshot - no lines array is ever materialized.
            Log.Write("mirror drifted in buffer " + buf + " (VS " + snapshot.LineCount
                      + " lines, nvim " + theirLines + ") - resending");

            // A line or two apart is an operator in flight. A gap this size is
            // not something any single edit explains, but it is also what an
            // external file change or a reload produces - and the right response
            // to that is to re-prime nvim from Visual Studio, not to stop the
            // mirror permanently. Only repeated failure to converge is a reason
            // to give up.
            int drifts = Interlocked.Increment(ref _consecutiveDrifts);
            if (drifts >= 5)
            {
                TripApply("the mirror kept diverging after " + drifts
                          + " repairs, so repairing it is not working");
            }

            if (WildlyApart(snapshot.LineCount, theirLines))
            {
                Log.Write("large drift in buffer " + buf + " ("
                          + Math.Abs(snapshot.LineCount - theirLines)
                          + " lines apart) - re-priming nvim from Visual Studio");
            }

            // Tracked like any other write, and that is the whole point. Left
            // untracked, this resend's own event came back looking like nvim's
            // work: it replaces the range nvim *had* with the lines VS has, so
            // applying it grew VS by exactly the gap, which widened the gap,
            // which triggered the next resend. Fifty-two lines every five
            // hundred milliseconds, without limit.
            await TrackWriteAsync(buf, () => SetAllLinesAsync(buf, snapshot))
                .ConfigureAwait(false);

            // The repair replaced every line, and nvim's manual folds went
            // with them: tell the fold synchronizer, as the prime does.
            NotifyPrimed();
        }
        catch (Exception ex)
        {
            Log.Write("mirror verify failed", ex);
        }
        finally
        {
            Volatile.Write(ref _verifying, 0);
        }
    }
#pragma warning restore VSTHRD100

    /// <summary>
    /// sha256 of the lines joined with '\n' - the exact convention
    /// vsneo.buffer_hash uses on the nvim side, so equal digests mean equal
    /// line arrays. Runs on the verify timer thread, never the key path.
    ///
    /// Streamed: each line is copied out of the snapshot in chunks through
    /// one reused char buffer, encoded through one reused byte buffer, and
    /// fed to the hash as it goes. It used to build the whole file three
    /// times over (a string per line, the joined text, its UTF-8 bytes); on
    /// any file past a few thousand lines two of those land on the large
    /// object heap, which only a full (gen2) collection frees - in
    /// devenv's heap, the kind of pause that reads as Visual Studio
    /// stuttering - and this ran on every editing pause.
    ///
    /// The buffers are this mirror's own: Verify never overlaps itself
    /// (_verifying), and mirrors verify concurrently.
    /// </summary>
    private string HashSnapshot(ITextSnapshot snapshot)
    {
        var chars = _hashChars ??= new char[HashChunk];
        var bytes = _hashBytes ??= new byte[System.Text.Encoding.UTF8.GetMaxByteCount(HashChunk)];
        // One encoder across the whole text, as a single GetBytes over the
        // joined string would be: a surrogate pair split between chunks
        // still encodes as one character.
        var encoder = _hashEncoder ??= System.Text.Encoding.UTF8.GetEncoder();
        encoder.Reset();

        // Reused across passes like the buffers: a fresh SHA256 object per
        // editing pause showed up in allocation profiles for no benefit.
        var sha = _hash ??= System.Security.Cryptography.SHA256.Create();
        sha.Initialize();
        {
            int count;
            int lineCount = snapshot.LineCount;
            for (int i = 0; i < lineCount; i++)
            {
                var line = snapshot.GetLineFromLineNumber(i);
                int pos = line.Start.Position, end = line.End.Position;
                while (pos < end)
                {
                    int n = Math.Min(chars.Length, end - pos);
                    snapshot.CopyTo(pos, chars, 0, n);
                    pos += n;
                    count = encoder.GetBytes(chars, 0, n, bytes, 0, false);
                    sha.TransformBlock(bytes, 0, count, null, 0);
                }

                // The separator goes through the encoder too, so anything it
                // is still holding comes out before it, in order.
                if (i < lineCount - 1)
                {
                    chars[0] = '\n';
                    count = encoder.GetBytes(chars, 0, 1, bytes, 0, false);
                    sha.TransformBlock(bytes, 0, count, null, 0);
                }
            }

            count = encoder.GetBytes(chars, 0, 0, bytes, 0, true);
            sha.TransformFinalBlock(bytes, 0, count);
            return ToHex(sha.Hash);
        }
    }

    private const int HashChunk = 4096;
    private char[]? _hashChars;
    private byte[]? _hashBytes;
    private System.Text.Encoder? _hashEncoder;
    private System.Security.Cryptography.SHA256? _hash;

    private static string ToHex(byte[] digest)
    {
        const string Digits = "0123456789abcdef";
        var hex = new char[digest.Length * 2];
        for (int i = 0; i < digest.Length; i++)
        {
            hex[2 * i] = Digits[digest[i] >> 4];
            hex[2 * i + 1] = Digits[digest[i] & 0xF];
        }

        return new string(hex);
    }

    /// <summary>
    /// Long enough that it never runs mid-keystroke, short enough that the next
    /// motion after you stop typing is already working against correct text.
    /// </summary>
    private const int VerifyDelayMs = 500;

    /// <summary>
    /// Repair that is working converges in a single pass, so a second and a third
    /// mean the two ends disagree about something resending cannot settle. Backing
    /// off keeps that from becoming a permanent write loop against nvim - which is
    /// exactly what it became: 2Hz for ninety seconds on an editor nobody was
    /// touching, and it would have run all session.
    ///
    /// Doubling from 500ms, capped at half a minute. Convergence resets it, so the
    /// ordinary case still re-checks promptly.
    /// </summary>
    private int VerifyDelay
    {
        get
        {
            int drifts = Math.Min(Volatile.Read(ref _consecutiveDrifts), 6);
            return Math.Min(VerifyDelayMs << drifts, 30000);
        }
    }

    private void ScheduleVerify()
    {
        try
        { _verify.Change(VerifyDelay, Timeout.Infinite); }
        catch (ObjectDisposedException) { }
    }
}
