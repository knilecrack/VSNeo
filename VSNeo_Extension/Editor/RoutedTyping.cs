using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using VSNeo_Extension.Nvim;

namespace VSNeo_Extension.Editor
{
    /// <summary>
    /// Visual Studio's completion for text typed through nvim
    /// (vsneo_insert_via_nvim).
    ///
    /// Completion fires on the typed-character command going through the
    /// view's command chain, never on a buffer change - and a character typed
    /// through nvim reaches Visual Studio only as a buffer change (a remote
    /// edit). So this does what the editor's own command handler does on
    /// TYPECHAR, after the character lands: TriggerCompletion when no session
    /// is open, then OpenOrUpdate with the character, so the list opens and
    /// filters as it would for typing. A routed Backspace updates an open list
    /// as a deletion. Committing (Enter, Tab, a commit character) is never
    /// routed: those keys go to Visual Studio while a list is open, and the
    /// commit is a Visual Studio edit that reaches nvim through the mirror.
    ///
    /// The key path only records what it routed (NoteTyped, NoteDeleted: a
    /// list append). Matching happens on the buffer change, the broker call is
    /// posted at Decoration priority - completion is Visual Studio work and
    /// stays off KeyResponse - and only the newest landed key of a burst
    /// triggers, which is where the list ends up anyway.
    /// </summary>
    internal sealed class RoutedTyping
    {
        // A routed key whose edit never arrives (an abbreviation rewrote it, a
        // mapping swallowed it) must not linger and match a later edit.
        private const int StaleMs = 1000;

        private readonly IWpfTextView _view;
        private readonly IntelliSenseGate _gate;
        private readonly List<Pending> _pending = new List<Pending>();   // UI thread
        private bool _subscribed;
        private Landed? _landed;      // newest landed key not yet triggered
        private bool _posted;

        private struct Pending
        {
            public char Char;          // '\0' for a deletion
            public int Ticks;
        }

        private struct Landed
        {
            public char Char;
            public ITrackingPoint Location;
        }

        private RoutedTyping(IWpfTextView view, IntelliSenseGate gate)
        {
            _view = view;
            _gate = gate;
        }

        public static RoutedTyping For(IWpfTextView view, IntelliSenseGate gate) =>
            view.Properties.GetOrCreateSingletonProperty(() => new RoutedTyping(view, gate));

        /// <summary>Key path: a printable character was sent to nvim.</summary>
        public void NoteTyped(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            Note(text[text.Length - 1]);
        }

        /// <summary>Key path: a Backspace was sent to nvim.</summary>
        public void NoteDeleted() => Note('\0');

        private void Note(char c)
        {
            if (_gate?.AsyncCompletion == null) return;
            if (!_subscribed)
            {
                _subscribed = true;
                _view.TextBuffer.Changed += OnBufferChanged;
                _view.Closed += OnClosed;
            }
            int now = Environment.TickCount;
            _pending.RemoveAll(p => unchecked(now - p.Ticks) > StaleMs);
            _pending.Add(new Pending { Char = c, Ticks = now });
        }

        private void OnClosed(object sender, EventArgs e)
        {
            _view.TextBuffer.Changed -= OnBufferChanged;
            _view.Closed -= OnClosed;
            _subscribed = false;
            _pending.Clear();
        }

        private void OnBufferChanged(object sender, TextContentChangedEventArgs e)
        {
            if (_pending.Count == 0) return;
            if (!(e.EditTag is string tag && tag == "VSNeo")) return;

            foreach (var change in e.Changes)
            {
                if (_pending.Count == 0) break;

                // Remote edits replace whole lines; the key's effect is the
                // part that differs. Only small changes are a typed key's.
                string oldText = change.OldText, newText = change.NewText;
                if (oldText.Length > 4096 || newText.Length > 4096) continue;

                int prefix = 0;
                int max = Math.Min(oldText.Length, newText.Length);
                while (prefix < max && oldText[prefix] == newText[prefix]) prefix++;
                int suffix = 0;
                while (suffix < max - prefix
                       && oldText[oldText.Length - 1 - suffix] == newText[newText.Length - 1 - suffix])
                    suffix++;
                int inserted = newText.Length - prefix - suffix;
                int removed = oldText.Length - prefix - suffix;

                char landed;
                if (inserted > 0) landed = newText[prefix + inserted - 1];
                else if (removed > 0) landed = '\0';
                else continue;

                int match = _pending.FindIndex(p => p.Char == landed);
                if (match < 0) continue;
                _pending.RemoveRange(0, match + 1);   // older ones never landed

                int position = change.NewPosition + prefix + inserted;
                _landed = new Landed
                {
                    Char = landed,
                    Location = e.After.CreateTrackingPoint(position, PointTrackingMode.Positive),
                };
            }

            if (_landed != null && !_posted)
            {
                _posted = true;
#pragma warning disable VSTHRD001, VSTHRD110
                _view.VisualElement.Dispatcher.BeginInvoke(
                    Infrastructure.UiPriority.Decoration, new Action(Trigger));
#pragma warning restore VSTHRD001, VSTHRD110
            }
        }

        private void Trigger()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            using var perf = Infrastructure.Perf.Time("RoutedTyping.Trigger");
            _posted = false;
            var landed = _landed;
            _landed = null;
            if (landed == null || _view.IsClosed || !_view.HasAggregateFocus) return;

            var session = VSNeo_ExtensionPackage.Session;
            if (session == null || !session.State.InsertViaNvim) return;
            if (session.State.Mode is not (VimMode.Insert or VimMode.Replace)) return;

            var broker = _gate.AsyncCompletion;
            if (broker == null) return;

            try
            {
                var snapshot = _view.TextBuffer.CurrentSnapshot;
                var location = landed.Value.Location.GetPoint(snapshot);
                var c = landed.Value.Char;

                var completion = broker.GetSession(_view);
                if (c == '\0')
                {
                    // Deletion: filters an open list; never opens one, as in
                    // Visual Studio's own Backspace.
                    if (completion == null || completion.IsDismissed) return;
                    completion.OpenOrUpdate(
                        new CompletionTrigger(CompletionTriggerReason.Deletion, snapshot),
                        location, CancellationToken.None);
                    return;
                }

                var trigger = new CompletionTrigger(CompletionTriggerReason.Insertion, snapshot, c);
                bool opened = false;
                if (completion == null || completion.IsDismissed)
                {
                    completion = broker.TriggerCompletion(_view, trigger, location, CancellationToken.None);
                    opened = completion != null;
                }
                completion?.OpenOrUpdate(trigger, location, CancellationToken.None);

                var line = location.GetContainingLine();
                Infrastructure.Log.Key("completion " + (opened ? "triggered" : completion == null ? "declined" : "updated")
                                       + " for '" + c + "' at " + line.LineNumber + ":" + (location.Position - line.Start.Position)
                                       + " (caret " + _view.Caret.Position.BufferPosition.Position
                                       + ", location " + location.Position + ")");
            }
            catch (Exception ex)
            {
                Infrastructure.Log.Write("completion trigger for routed typing failed", ex);
            }
        }
    }
}
