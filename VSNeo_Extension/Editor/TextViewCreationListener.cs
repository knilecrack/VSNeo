using System;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace VSNeo_Extension.Editor
{
    /// <summary>
    /// This ran eagerly and froze Visual Studio once already. The rule now: this
    /// method does bookkeeping only. No service resolution, no process start, no
    /// RPC, no JoinableTaskFactory.Run, no .Result, no .Wait().
    /// </summary>
    [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class TextViewCreationListener : IWpfTextViewCreationListener
    {
        [Import]
        internal CursorSynchronizer CursorSync { get; set; } = null!;

        [Import]
        internal ViewportSynchronizer ViewportSync { get; set; } = null!;

        /// <summary>
        /// Visual Studio's undo is authoritative, so nvim's edits have to enter it as
        /// proper transactions rather than as loose buffer changes.
        /// </summary>
        [Import]
        internal Microsoft.VisualStudio.Text.Operations.ITextUndoHistoryRegistry UndoRegistry { get; set; } = null!;

        /// <summary>Resolves a text buffer back to the file on disk it came from.</summary>
        [Import]
        internal Microsoft.VisualStudio.Text.ITextDocumentFactoryService DocumentFactory { get; set; } = null!;

        /// <summary>Maps a WPF text view back to its native adapter (and window frame).</summary>
        [Import]
        internal Microsoft.VisualStudio.Editor.IVsEditorAdaptersFactoryService EditorAdapters { get; set; } = null!;

        /// <summary>Which document nvim's window is currently showing; null until the first one is. UI thread only.</summary>
        private static Microsoft.VisualStudio.Text.ITextBuffer? _shownBuffer;

        /// <summary>
        /// The mirror for <see cref="_shownBuffer"/>, kept so the snap-back can
        /// reach its nvim handle without a dictionary lookup. UI thread only.
        /// </summary>
        private static BufferMirror _shownMirror = null!;

        /// <summary>
        /// The path nvim's window is supposed to be showing. Written before every
        /// Visual Studio-initiated nvim_win_set_buf, so the BufEnter that switch
        /// provokes is recognised as our own; anything else arriving at
        /// <see cref="OnNvimBufferSwitched"/> is nvim moving on its own - a
        /// file-mark jump, a cross-file &lt;C-o&gt;, :b, gf - and is yanked back.
        /// Visual Studio owns which document is shown: it cannot follow nvim to
        /// a file that may not even be open, and the alternative (opening it)
        /// makes an editor tab appear from a keystroke like '0, which reads as
        /// haunted. Any thread; Volatile-guarded.
        /// </summary>
        private static string? _expectedNvimPath;

        /// <summary>Session whose BufferSwitched the snap-back is subscribed to; null when none.</summary>
        private static Nvim.NvimSession _watchedSession = null!;

        /// <summary>
        /// Views that were focused before nvim finished starting. They are attached
        /// as soon as the session reports ready, rather than waiting for a focus
        /// bounce that may never come.
        /// </summary>
        private static readonly System.Collections.Generic.List<IWpfTextView> _pendingFocus
            = new System.Collections.Generic.List<IWpfTextView>();

        private static int _readyHooked;

        /// <summary>
        /// nvim moved its window without Visual Studio asking: a file-mark jump
        /// ('0-'9, 'A-'Z), a cross-file &lt;C-o&gt;, :b, gf. Visual Studio cannot
        /// follow - the file may not even be open - so nvim is pointed back at
        /// the document on screen. Runs on the RPC read thread; the requests it
        /// sends are the whole reply, so nothing here blocks.
        /// </summary>
        private static void OnNvimBufferSwitched(string path)
        {
            var expected = System.Threading.Volatile.Read(ref _expectedNvimPath);
            if (expected == null) return;   // nothing shown yet; the startup buffer is nvim's own

            string normalized = path;
            if (normalized.Length > 0)
            {
                try { normalized = System.IO.Path.GetFullPath(normalized); }
                catch { /* keep raw; the equality check below just fails and we snap back */ }
            }
            if (string.Equals(normalized, expected, StringComparison.OrdinalIgnoreCase)) return;

            var mirror = System.Threading.Volatile.Read(ref _shownMirror);
            var session = System.Threading.Volatile.Read(ref _watchedSession);
            if (mirror == null || session == null) return;

            Infrastructure.Log.Write("nvim switched buffers on its own ("
                + (path.Length == 0 ? "<unnamed>" : path) + ") - snapping back to " + expected);
#pragma warning disable VSSDK007
            // Fire-and-forget on purpose, same MEF-listener constraint as Attach:
            // no package JoinableTaskFactory is reachable from here, and the
            // snap-back has no caller to report to.
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    long handle = await mirror.EnsureCreatedAsync();
                    await session.RequestAsync("nvim_win_set_buf", 0, handle);
                }
                catch (Exception ex)
                {
                    Infrastructure.Log.Write("buffer snap-back failed", ex);
                }
            });
#pragma warning restore VSSDK007
        }

        /// <summary>Idempotent: the session can be swapped by a reconnect.</summary>
        private void WatchBufferSwitches(Nvim.NvimSession session)
        {
            if (ReferenceEquals(
                    System.Threading.Volatile.Read(ref _watchedSession), session)) return;
            session.State.BufferSwitched += OnNvimBufferSwitched;
            System.Threading.Volatile.Write(ref _watchedSession, session);
        }

        /// <summary>
        /// The path nvim should currently be showing, for the synchronizers
        /// dropping reports that describe any other buffer. Null until the
        /// first Visual Studio-initiated switch.
        /// </summary>
        internal static string? ExpectedNvimPath =>
            System.Threading.Volatile.Read(ref _expectedNvimPath);

        /// <summary>
        /// The path nvim should know this buffer by. Filetype detection, file marks
        /// and every plugin key off it, so an unnamed buffer is a buffer nvim cannot
        /// reason about. Not every buffer has one - scratch and output windows do not.
        /// </summary>
        private string? PathOf(Microsoft.VisualStudio.Text.ITextBuffer buffer) =>
            DocumentFactory != null
            && DocumentFactory.TryGetTextDocument(buffer, out var document)
                ? document.FilePath
                : null;

        public void TextViewCreated(IWpfTextView textView)
        {
            // Hook focus unconditionally. The package loads in the background, so a
            // view created before it would otherwise never get a mirror at all.
            textView.GotAggregateFocus += OnGotFocus;
            textView.Closed += (s, e) => textView.GotAggregateFocus -= OnGotFocus;
        }

        private void OnGotFocus(object sender, System.EventArgs e)
        {
            // GotAggregateFocus fires on the UI thread, and Attach below requires it.
            ThreadHelper.ThrowIfNotOnUIThread();

            var view = (IWpfTextView)sender;

            // Feed SplitNavigator's "last active tab per group" memory. The lookup
            // goes through the view itself (IVsTextViewEx.GetWindowFrame), so it is
            // exact and immune to whatever the shell's selection element is doing.
            SplitNavigator.RememberViewFrame(view, EditorAdapters);

            var session = VSNeo_ExtensionPackage.Session;
            if (session == null || !session.IsReady)
            {
                // Focus arrived before nvim was up. Queue the view so it is attached
                // as soon as the session is ready; without this the key processor
                // keeps swallowing motions into nvim's startup buffer and the editor
                // appears frozen.
                lock (_pendingFocus)
                {
                    if (!_pendingFocus.Contains(view))
                        _pendingFocus.Add(view);
                }

                // The session may not even exist yet - the startup document is
                // focused long before the package finishes loading - so listen
                // to the package's static broadcast rather than an instance
                // event that is not there to subscribe to.
                if (System.Threading.Interlocked.Exchange(ref _readyHooked, 1) == 0)
                    VSNeo_ExtensionPackage.SessionReadyChanged += OnSessionReady;

                Infrastructure.Log.Write(
                    "focus before ready, mirror queued (session="
                    + (session == null ? "null" : "notReady") + ")");
                return;
            }

            Attach(view, session);
        }

        /// <summary>Called when the nvim session becomes ready, on any thread.</summary>
        private void OnSessionReady(bool ready)
        {
            if (!ready) return;

            System.Collections.Generic.List<IWpfTextView> pending;
            lock (_pendingFocus)
            {
                pending = new System.Collections.Generic.List<IWpfTextView>(_pendingFocus);
                _pendingFocus.Clear();
            }

            if (pending.Count == 0) return;

            // VSSDK007 wants the package's own JoinableTaskFactory, but this is a
            // MEF listener, not the AsyncPackage, so ThreadHelper's is the only one
            // in reach.
#pragma warning disable VSSDK007
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var session = VSNeo_ExtensionPackage.Session;
                if (session == null || !session.IsReady) return;

                foreach (var view in pending)
                {
                    if (view.IsClosed) continue;
                    try
                    {
                        Attach(view, session);
                    }
                    catch (Exception ex)
                    {
                        Infrastructure.Log.Write("could not attach a queued view", ex);
                    }
                }
            });
#pragma warning restore VSSDK007
        }

        private void Attach(IWpfTextView view, VSNeo_Extension.Nvim.NvimSession session)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var buffer = view.TextBuffer;

            // Not GetOrCreateSingletonProperty on the text buffer. That gives one
            // mirror per ITextBuffer, and Visual Studio makes a new ITextBuffer for a
            // document it has already shown - so two mirrors ended up holding the same
            // nvim buffer and spent the session overwriting each other. ForDocument
            // keys on the file instead, which is what the nvim buffer is keyed on.
            // PathOf returns null for unnamed buffers, and BufferMirror treats that
            // as the anonymous key.
            var mirror = BufferMirror.ForDocument(
                buffer, session, PathOf(buffer), CursorSync, UndoRegistry);

            WatchBufferSwitches(session);

            CursorSync.SetActiveView(view);
            ViewportSync.SetActiveView(view);

            // Refocusing the document nvim is already showing costs nothing. Focus
            // bounces constantly - Solution Explorer, the find box, any tool window -
            // and each of those used to resend an entire file.
            if (ReferenceEquals(_shownBuffer, buffer))
            {
                CursorSync.SyncCaretToNvim();
                return;
            }

            // Each document has its own nvim buffer, so switching documents is now
            // just pointing nvim's window at the one that already holds this file.
            // The contents were sent once, when the buffer was created.
            // Same MEF-listener constraint as above: no package JoinableTaskFactory
            // is reachable from here.
#pragma warning disable VSSDK007
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    long handle = await mirror.EnsureCreatedAsync();

                    // Switching nvim's window makes the companion's BufEnter push
                    // report the cursor and topline nvim last had for this buffer,
                    // and applying those would stomp the navigation target Visual
                    // Studio just put the caret on (gd into another file). The
                    // barriers keep that stale state off the view until nvim has
                    // caught up with the caret pushed below.
                    CursorSync.BeginBufferSwitch();
                    ViewportSync.BeginBufferSwitch();

                    // Declared before the RPC: the BufEnter that this switch
                    // provokes can race back faster than the response, and the
                    // snap-back must recognise it as our own. GetFullPath matches
                    // the hub's normalization of the BufEnter report.
                    var expectedPath = PathOf(buffer);
                    if (!string.IsNullOrEmpty(expectedPath))
                    {
                        try { expectedPath = System.IO.Path.GetFullPath(expectedPath); }
                        catch { /* keep raw */ }
                    }
                    System.Threading.Volatile.Write(ref _expectedNvimPath, expectedPath);

                    // One window, switching buffers, rather than a window per
                    // document. The jumplist and the alternate file live in the
                    // window, and in Vim they deliberately span files - a window each
                    // would give every document its own private jumplist, which is
                    // not how Vim behaves.
                    await session.RequestAsync("nvim_win_set_buf", 0, handle);

                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                    // Only now is nvim's window actually showing this document.
                    // Recording it earlier meant a failure here latched: the retry on
                    // the next focus was skipped, and nvim was left on the empty
                    // startup buffer for the rest of the session.
                    _shownBuffer = buffer;
                    _shownMirror = mirror;
                    CursorSync.SyncCaretToNvim(force: true);
                }
                catch (Exception ex)
                {
                    Infrastructure.Log.Write("could not show the document in nvim", ex);
                }
            });
#pragma warning restore VSSDK007
        }
    }
}

