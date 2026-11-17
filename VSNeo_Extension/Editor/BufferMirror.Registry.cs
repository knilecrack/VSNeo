using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using VSNeo_Extension.Infrastructure;
using VSNeo_Extension.Nvim;

namespace VSNeo_Extension.Editor;

// Registry and creation: the one-writer-per-file rule and the nvim buffer lifecycle.
internal sealed partial class BufferMirror
{
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
    /// <summary>
    /// One nvim buffer per <em>file</em>, not per ITextBuffer.
    ///
    /// Visual Studio makes a fresh ITextBuffer for the same path more often than
    /// you would expect - reopening a closed document, peek definition, diff
    /// views - and each one used to create its own nvim buffer and then try to
    /// claim a name nvim had already given away. nvim answers that with
    /// "E95: Buffer with this name already exists", creation threw, and because
    /// nothing logged that path the only visible symptom was that nvim's window
    /// still held the empty startup buffer: every window-relative motion did
    /// nothing and every caret push came back "cursor line out of range".
    /// </summary>
    private static readonly Dictionary<string, Task<long>> Registry
        = new Dictionary<string, Task<long>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Which mirror currently owns each nvim buffer, keyed exactly as
    /// <see cref="Registry"/> is.
    ///
    /// Sharing the handle was deliberate and correct; sharing it between two
    /// *live* mirrors was not. Visual Studio hands out a fresh ITextBuffer for
    /// the same path more often than you would expect - reopening a document is
    /// enough - and both mirrors then held nvim buffer 2, each resending its own
    /// snapshot over the other's every 500ms. Measured on an idle editor: two
    /// mirrors alternating at 2Hz for ninety seconds, 57 lines against 31,
    /// neither ever winning.
    /// </summary>
    private static readonly Dictionary<string, BufferMirror> Live
        = new Dictionary<string, BufferMirror>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Buffers with no file on disk cannot collide, so they key on identity.</summary>
    private static string KeyFor(ITextBuffer buffer, string? filePath) =>
        // net472's IsNullOrEmpty carries no [NotNullWhen], hence the forgiveness.
        string.IsNullOrEmpty(filePath)
            ? " anonymous:" + buffer.GetHashCode()
            : filePath!;

    /// <summary>
    /// The one mirror for this document, creating it if this is the first view of
    /// it and retiring any earlier mirror bound to a stale ITextBuffer.
    ///
    /// Always go through here rather than constructing one per text buffer: one
    /// nvim buffer must have exactly one writer, or the drift repair on each side
    /// spends the session undoing the other's.
    /// </summary>
    public static BufferMirror ForDocument(
        ITextBuffer buffer, NvimSession session, string? filePath,
        CursorSynchronizer cursorSync,
        Microsoft.VisualStudio.Text.Operations.ITextUndoHistoryRegistry undoRegistry)
    {
        // Called from the view attach (UI thread); retiring a mirror closes
        // its insert transaction, which the undo history wants there.
        ThreadHelper.ThrowIfNotOnUIThread();
        string key = KeyFor(buffer, filePath);

        lock (Live)
        {
            if (Live.TryGetValue(key, out var existing))
            {
                if (!existing._disposed && ReferenceEquals(existing._buffer, buffer))
                {
                    return existing;
                }

                Log.Write("retiring the previous mirror for " + (filePath ?? "<unnamed>")
                          + ": Visual Studio has given this document a new text buffer");
                existing.Dispose();
            }

            var mirror = new BufferMirror(buffer, session, filePath, cursorSync, undoRegistry);
            Log.Write("created mirror " + mirror.GetHashCode() + " for " + (filePath ?? "<unnamed>"));
            Live[key] = mirror;
            return mirror;
        }
    }

    public async Task<long> EnsureCreatedAsync()
    {
        long existing = Handle;
        if (existing >= 0)
        {
            return existing;
        }

        string key = KeyFor(_buffer, _filePath);

        Task<long> creation;
        bool ours = false;
        lock (Registry)
        {
            if (!Registry.TryGetValue(key, out creation))
            {
                creation = CreateAsync();
                Registry[key] = creation;
                ours = true;
            }
        }

        try
        {
            long handle = await creation.ConfigureAwait(false);
            System.Threading.Volatile.Write(ref _handle, handle);

            // A cached creation means an earlier mirror for this path already
            // filled the nvim buffer from *its* snapshot, and that mirror is now
            // retired. This document is the one on screen, so its text is the text
            // that counts - without this the adopted buffer keeps the dead view's
            // contents and every motion is computed against the wrong file.
            if (!ours && !_disposed)
            {
                Log.Write("adopting nvim buffer " + handle + " for "
                          + (_filePath ?? "<unnamed>") + " - re-priming");
                await PrimeAsync(handle).ConfigureAwait(false);
            }

            return handle;
        }
        catch (Exception ex)
        {
            // Do not leave a faulted task cached, or this document can never
            // recover: drop it so the next focus retries from scratch.
            lock (Registry)
            {
                if (Registry.TryGetValue(key, out var current) && ReferenceEquals(current, creation))
                {
                    Registry.Remove(key);
                }
            }

            Log.Write("could not create an nvim buffer for " + (_filePath ?? "<unnamed>"), ex);
            throw;
        }
    }

    private async Task<long> CreateAsync()
    {
        // nvim may already hold a buffer for this file: :b, gf or a plugin
        // loaded it before Visual Studio ever showed the document. Naming a
        // fresh buffer the same path fails with E95, and a second buffer
        // for one path splits the edit stream - so adopt nvim's. Same
        // acwrite guard, same prime: Visual Studio's text wins, which is
        // right - nvim's self-loaded copy is disk content, and any unsaved
        // edits a plugin made there are discarded by design. Visual Studio
        // owns files.
        if (!string.IsNullOrEmpty(_filePath))
        {
            var found = await _session.RequestAsync(
                "nvim_exec_lua", "return vsneo.find_buffer(...)",
                new object[] { _filePath! }).ConfigureAwait(false);

            long adopted = 0;
            try
            {
                if (found != null)
                {
                    adopted = Convert.ToInt64(found);
                }
            }
            catch { /* a non-numeric answer just means "not found" */ }

            if (adopted > 0)
            {
                Log.Write("adopting nvim buffer " + adopted + " for " + _filePath
                          + " - nvim loaded it first");
                await _session.RequestAsync(
                    "nvim_buf_set_option", adopted, "buftype", "acwrite")
                    .ConfigureAwait(false);
                System.Threading.Volatile.Write(ref _handle, adopted);
                await PrimeAsync(adopted).ConfigureAwait(false);
                ScheduleVerify();
                return adopted;
            }
        }

        // listed: true so :ls and :b see it like any other file.
        var created = await _session.RequestAsync("nvim_create_buf", true, false)
                                    .ConfigureAwait(false);

        long handle = created is NvimHandle h ? h.Id : Convert.ToInt64(created);
        if (handle <= 0)
        {
            throw new InvalidOperationException("nvim_create_buf returned " + created);
        }

        // acwrite routes :w to the BufWriteCmd the session installed, so nvim
        // cannot write this path from under Visual Studio.
        await _session.RequestAsync("nvim_buf_set_option", handle, "buftype", "acwrite")
                      .ConfigureAwait(false);

        if (!string.IsNullOrEmpty(_filePath))
        {
            try
            {
                // Non-null by the guard; net472's IsNullOrEmpty has no
                // [NotNullWhen] to prove it to the compiler.
                await _session.RequestAsync("nvim_buf_set_name", handle, _filePath!)
                              .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // An unnamed buffer costs filetype and file marks. Losing the
                // whole document to an exception costs everything, so carry on.
                Log.Write("could not name buffer " + handle + " " + _filePath, ex);
            }
        }

        // Published before priming, not after. OnRemoteLines discards events for
        // any other buffer by comparing against this handle, and it does that
        // before counting the echo - so while it was still -1 the prime's own
        // event was dropped uncounted and left the tally one high. The settled
        // Verify cleaned that up, which is why it showed as a stale echo rather
        // than as a bug.
        System.Threading.Volatile.Write(ref _handle, handle);

        await PrimeAsync(handle).ConfigureAwait(false);

        // Detection has to run after both the name and the contents are in place;
        // some filetypes are decided by the first line, not the extension.
        await _session.RequestAsync(
            "nvim_exec_lua",
            "vim.api.nvim_buf_call(..., function() vim.cmd('filetype detect') end)",
            new object[] { handle }).ConfigureAwait(false);

        Log.Write("nvim buffer " + handle + " created for " + (_filePath ?? "<unnamed>"));

        // Check the two agree once things settle, rather than waiting for the
        // first edit to find out they never did.
        ScheduleVerify();
        return handle;
    }
}
