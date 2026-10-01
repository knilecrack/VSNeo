using System;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Operations;
using VSNeo_Extension.Infrastructure;
using VSNeo_Extension.Nvim;

namespace VSNeo_Extension.Editor
{
    /// <summary>
    /// Vim's undo unit for an insert session, over Visual Studio's undo history,
    /// without holding a transaction open.
    ///
    /// With typing routed through nvim (vsneo_insert_via_nvim) every typed
    /// character comes back as its own remote edit, each its own transaction:
    /// u would take one character at a time. Holding one transaction open for
    /// the whole insert is what 1.6.2 did, and a real document's history (an
    /// adapter over the shell undo manager) could not close it - u died. So the
    /// transactions stay as they are, and this counts them instead: every
    /// transaction added while in insert joins the session's group, and u
    /// undoes the group's count in one go.
    ///
    /// The count follows UndoTransactionCompleted, so Visual Studio's own
    /// edits (a completion commit) join the group too. Any undo or redo that
    /// did not go through u / Ctrl+R (Ctrl+Z, Edit.Undo) desynchronises the
    /// count from the history, and the stacks are dropped: u falls back to one
    /// step per press until the next insert builds a group again.
    /// </summary>
    internal sealed class UndoGroups
    {
        private readonly ITextUndoHistory _history;
        private readonly UndoGroupStack _stack = new UndoGroupStack();
        private bool _ours;   // an undo/redo of ours is running; UI thread

        private UndoGroups(ITextUndoHistory history)
        {
            _history = history;
            history.UndoTransactionCompleted += OnCompleted;
            history.UndoRedoHappened += OnUndoRedo;
        }

        /// <summary>UI thread. Subscribes once per buffer.</summary>
        public static UndoGroups Ensure(ITextBuffer buffer, ITextUndoHistory history)
        {
            if (buffer.Properties.TryGetProperty(typeof(UndoGroups), out UndoGroups existing)
                && ReferenceEquals(existing._history, history))
                return existing;

            var groups = new UndoGroups(history);
            buffer.Properties[typeof(UndoGroups)] = groups;
            return groups;
        }

        public static UndoGroups? TryGet(ITextBuffer buffer) =>
            buffer.Properties.TryGetProperty(typeof(UndoGroups), out UndoGroups groups) ? groups : null;

        private static bool Grouping()
        {
            var session = VSNeo_ExtensionPackage.Session;
            if (session == null || !session.State.InsertViaNvim) return false;
            var mode = session.State.Mode;
            return mode == VimMode.Insert || mode == VimMode.Replace;
        }

        private void OnCompleted(object sender, TextUndoTransactionCompletedEventArgs e)
        {
            if (e.Result != TextUndoTransactionCompletionResult.TransactionAdded) return;
            _stack.Added(Grouping());
        }

        private void OnUndoRedo(object sender, TextUndoRedoEventArgs e)
        {
            if (_ours) return;
            _stack.Reset();
        }

        /// <summary>Leaving insert closes the session's group.</summary>
        public void InsertEnded() => _stack.InsertEnded();

        /// <summary>Ctrl+Z and friends: the count no longer describes the history.</summary>
        public void Reset() => _stack.Reset();

        /// <summary>UI thread. One u: the whole group on top of the stack.</summary>
        public void Undo() => Step(undo: true);

        /// <summary>UI thread. One Ctrl+R.</summary>
        public void Redo() => Step(undo: false);

        private void Step(bool undo)
        {
            int count = undo ? _stack.TakeUndo() : _stack.TakeRedo();
            if (count > 1) Infrastructure.Log.Key((undo ? "undo" : "redo") + " group of " + count);
            _ours = true;
            try
            {
                for (int i = 0; i < count; i++)
                {
                    if (undo ? !_history.CanUndo : !_history.CanRedo) break;
                    if (undo) _history.Undo(1);
                    else _history.Redo(1);
                }
            }
            finally
            {
                _ours = false;
            }
        }
    }
}
