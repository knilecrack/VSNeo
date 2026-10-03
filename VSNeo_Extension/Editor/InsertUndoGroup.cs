namespace VSNeo_Extension.Editor
{
    /// <summary>
    /// One insert session as one undo step, without holding anything open.
    ///
    /// Vim undoes a change through insert mode in one step: c3wXYZ&lt;Esc&gt;
    /// then u restores the three words. Here the change's deletion is one
    /// Visual Studio undo step (the mirror's transaction) and the typing is
    /// Visual Studio's own, in word-sized steps - so u took two presses, a
    /// long insert several. Holding one transaction open across the session
    /// failed live (1.6.2): the shell-backed document history refused to
    /// complete it and an open transaction refuses Undo.
    ///
    /// This records buffer *states* instead. A state is the snapshot's
    /// ReiteratedVersionNumber: after an undo or redo it is the number of the
    /// earlier version whose text the buffer now holds again, so it names the
    /// same text whatever path led back to it. Begin notes the state as insert
    /// is entered, End the state as it is left; u then walks Visual Studio's
    /// steps from the end state back to the start state, and &lt;C-r&gt; from
    /// the start back to the end. Only the last completed session is kept:
    /// a change elsewhere leaves the buffer in neither state, and the walk
    /// declines in favour of a single step. Pure, UI-thread only, and
    /// unit-tested without Visual Studio.
    /// </summary>
    internal sealed class InsertUndoGroup
    {
        private int _open = -1;        // start state of the session in progress, -1 when none
        private int _start = -1;       // last completed session's states
        private int _end = -1;

        public bool IsOpen => _open >= 0;

        /// <summary>Insert entered at this state. Idempotent while a session is open.</summary>
        public void Begin(int state)
        {
            if (_open < 0) _open = state;
        }

        /// <summary>
        /// Insert left at this state. A session that changed nothing (i&lt;Esc&gt;)
        /// records no group and leaves the previous one in force.
        /// </summary>
        public void End(int state)
        {
            if (_open < 0) return;
            if (state != _open)
            {
                _start = _open;
                _end = state;
            }
            _open = -1;
        }

        /// <summary>
        /// Where one u (or &lt;C-r&gt;) should walk to from the current state, or
        /// null for an ordinary single step: null unless the buffer sits exactly
        /// at the group's end (for undo) or start (for redo).
        /// </summary>
        public int? Target(int current, bool undo)
        {
            if (_start < 0) return null;
            if (undo) return current == _end ? _start : (int?)null;
            return current == _start ? _end : (int?)null;
        }
    }
}
