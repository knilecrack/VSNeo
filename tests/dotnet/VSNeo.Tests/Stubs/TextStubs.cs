// The slice of Visual Studio's text model that the linked sources touch, so
// they compile here unchanged. Same names and shapes as the real types
// (Microsoft.VisualStudio.Text.Data); only the members actually used exist.
// Tests build lines through StubSnapshot.Line.

using System.Collections.Generic;

namespace Microsoft.VisualStudio.Text
{
    public interface ITextSnapshot
    {
        char this[int position] { get; }
        int LineCount { get; }
        ITextSnapshotLine GetLineFromLineNumber(int lineNumber);
        void CopyTo(int sourceIndex, char[] destination, int destinationIndex, int count);
    }

    public readonly struct SnapshotPoint(int position)
    {
        public int Position { get; } = position;
    }

    public interface ITextSnapshotLine
    {
        ITextSnapshot Snapshot { get; }
        SnapshotPoint Start { get; }
        SnapshotPoint End { get; }
        int Length { get; }
    }
}

namespace VSNeo.Tests.Stubs
{
    using Microsoft.VisualStudio.Text;

    /// <summary>
    /// A snapshot over one string, with a line placed at an offset inside it -
    /// so the absolute-position indexer that ColumnMapper's snapshot overloads
    /// read through is exercised for real, not with the line at position 0.
    /// </summary>
    internal sealed class StubSnapshot(string text) : ITextSnapshot
    {
        // Line starts, computed once: GetLineFromLineNumber must be O(1) like
        // the real snapshot, or benchmarks measure the stub instead of the code.
        private readonly int[] _lineStarts = BuildLineStarts(text);

        private static int[] BuildLineStarts(string text)
        {
            var starts = new List<int> { 0 };
            for (int i = 0; i < text.Length; i++)
                if (text[i] == '\n') starts.Add(i + 1);
            return starts.ToArray();
        }

        public char this[int position] => text[position];

        public int LineCount => _lineStarts.Length;

        public ITextSnapshotLine GetLineFromLineNumber(int lineNumber)
        {
            int start = _lineStarts[lineNumber];
            int next = text.IndexOf('\n', start);
            int end = next < 0 ? text.Length : next;
            return new StubLine(this, start, end - start);
        }

        public void CopyTo(int sourceIndex, char[] destination, int destinationIndex, int count) =>
            text.CopyTo(sourceIndex, destination, destinationIndex, count);

        public static ITextSnapshotLine Line(string line, string before = "prefix line\n", string after = "\nsuffix")
        {
            var snapshot = new StubSnapshot(before + line + after);
            return new StubLine(snapshot, before.Length, line.Length);
        }

        private sealed class StubLine(ITextSnapshot snapshot, int start, int length) : ITextSnapshotLine
        {
            public ITextSnapshot Snapshot => snapshot;
            public SnapshotPoint Start => new(start);
            public SnapshotPoint End => new(start + length);
            public int Length => length;
        }
    }
}
