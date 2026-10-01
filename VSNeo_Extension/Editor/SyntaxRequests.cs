using System;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.Text;
using VSNeo_Extension.Infrastructure;
using VSNeo_Extension.Nvim;

namespace VSNeo_Extension.Editor
{
    /// <summary>
    /// Answers nvim's vsneo_syntax request - the Roslyn text objects and
    /// motions (af, if, ac, ic, ]m, [m, ]M, [M, ]], [[). nvim sends the path,
    /// its cursor (1-based row, 0-based byte column), the operation and a
    /// count; the answer is [startRow, startByte, endRow, endByteInclusive,
    /// linewise] in nvim's terms, or nil when there is no target (no syntax
    /// tree for this file, nothing enclosing, nothing further).
    ///
    /// Runs off the UI thread: snapshots are immutable, and Roslyn's document
    /// for a snapshot is a thread-safe lookup. The mirror keeps nvim's lines
    /// and Visual Studio's identical, so row N is line N-1 of the snapshot.
    /// </summary>
    internal static class SyntaxRequests
    {
        public const string Method = "vsneo_syntax";

        /// <summary>The answer for a file with no Roslyn syntax tree.</summary>
        public const string NoTree = "no_tree";

        public static async Task<object?> HandleAsync(string method, object[] args)
        {
            if (method != Method) throw new NotSupportedException("unknown request " + method);
            if (args.Length < 5) throw new ArgumentException("vsneo_syntax wants path, row, col, op, count");

            var path = NvimStateHub.AsString(args[0]);
            int row = Convert.ToInt32(args[1]);
            int byteCol = Convert.ToInt32(args[2]);
            var op = NvimStateHub.AsString(args[3]) ?? string.Empty;
            int count = Convert.ToInt32(args[4]);

            var buffer = TextViewCreationListener.ShownBuffer;
            if (buffer == null || !IsBufferFor(buffer, path)) return null;

            var snapshot = buffer.CurrentSnapshot;
            if (row < 1 || row > snapshot.LineCount) return null;
            var line = snapshot.GetLineFromLineNumber(row - 1);
            int position = line.Start.Position + Math.Min(ColumnMapper.ByteToChar(line, byteCol), line.Length);

            // "no_tree", not null, for anything Roslyn does not own (C++, plain
            // text, and VB until SyntaxTargets learns it): the companion then
            // falls back - nvim's treesitter, Visual Studio's outlining regions,
            // the built-in motion - where null means "no target here", and a
            // C# ]m past the last method must not fall through to a native one.
            var document = snapshot.GetOpenDocumentInCurrentContextWithChanges();
            if (document == null) return NoTree;
            var root = await document.GetSyntaxRootAsync().ConfigureAwait(false);
            if (root == null || root.Language != Microsoft.CodeAnalysis.LanguageNames.CSharp) return NoTree;

            var target = SyntaxTargets.Find(root, position, op, count);
            if (target == null) return null;

            var t = target.Value;
            int last = t.End > t.Start ? t.End - 1 : t.Start;
            ToRowByte(snapshot, t.Start, out int startRow, out int startByte);
            ToRowByte(snapshot, last, out int endRow, out int endByte);
            return new object[] { (long)startRow, (long)startByte, (long)endRow, (long)endByte, t.Linewise };
        }

        private static bool IsBufferFor(ITextBuffer buffer, string? path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (!buffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument document)) return false;
            return string.Equals(NvimStateHub.NormalizePath(document.FilePath), NvimStateHub.NormalizePath(path),
                                 StringComparison.OrdinalIgnoreCase);
        }

        private static void ToRowByte(ITextSnapshot snapshot, int position, out int row, out int byteCol)
        {
            if (position > snapshot.Length) position = snapshot.Length;
            var line = snapshot.GetLineFromPosition(position);
            row = line.LineNumber + 1;
            byteCol = ColumnMapper.CharToByte(line, Math.Min(position - line.Start.Position, line.Length));
        }
    }
}
