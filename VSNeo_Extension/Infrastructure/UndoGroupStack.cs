using System.Collections.Generic;

namespace VSNeo_Extension.Infrastructure
{
    /// <summary>
    /// The counting behind Editor/UndoGroups, free of Visual Studio so it
    /// can be tested. Each entry is how many history steps one u takes.
    /// </summary>
    internal sealed class UndoGroupStack
    {
        // Deep enough for any session's u; the oldest entries fall off, and a
        // history step with no entry undoes alone.
        private const int MaxDepth = 1000;

        private readonly LinkedList<int> _undo = new LinkedList<int>();
        private readonly Stack<int> _redo = new Stack<int>();
        private bool _open;   // the top undo entry is the live insert session's

        public void Added(bool inInsert)
        {
            _redo.Clear();
            if (inInsert && _open)
            {
                _undo.Last!.Value++;
                return;
            }
            _undo.AddLast(1);
            if (_undo.Count > MaxDepth) _undo.RemoveFirst();
            _open = inInsert;
        }

        public void InsertEnded() => _open = false;

        public int TakeUndo()
        {
            _open = false;
            if (_undo.Count == 0) return 1;
            int n = _undo.Last!.Value;
            _undo.RemoveLast();
            _redo.Push(n);
            return n;
        }

        public int TakeRedo()
        {
            _open = false;
            if (_redo.Count == 0) return 1;
            int n = _redo.Pop();
            _undo.AddLast(n);
            return n;
        }

        public void Reset()
        {
            _undo.Clear();
            _redo.Clear();
            _open = false;
        }
    }
}
