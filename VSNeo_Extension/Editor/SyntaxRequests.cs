using System;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Threading;   // await TaskScheduler.Default
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
            if (document == null) return await FromCodeModelAsync(path!, snapshot, position, op, count).ConfigureAwait(false);
            var root = await document.GetSyntaxRootAsync().ConfigureAwait(false);
            if (root == null || root.Language != Microsoft.CodeAnalysis.LanguageNames.CSharp) return NoTree;

            return Answer(snapshot, SyntaxTargets.Find(root, position, op, count));
        }

        /// <summary>
        /// The fallback for files Roslyn does not own: Visual Studio's code
        /// model (EnvDTE FileCodeModel), which the C++ service implements - the
        /// file's functions and types with their ranges, no parser needed. A
        /// file outside every project (Miscellaneous Files) has none, and
        /// answers "no_tree" like any other.
        ///
        /// The code model is single-threaded COM: the walk runs on the UI
        /// thread, which is free while nvim waits for this answer.
        /// </summary>
        private static async Task<object?> FromCodeModelAsync(string path, ITextSnapshot snapshot, int position, string op, int count)
        {
            await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var elements = new System.Collections.Generic.List<CodeModelTargets.Element>();
            try
            {
                var dte = Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(Microsoft.VisualStudio.Shell.Interop.SDTE)) as EnvDTE.DTE;
                var item = dte?.Solution?.FindProjectItem(path);
                var model = item?.FileCodeModel;
                if (model == null) return NoTree;
                Collect(model.CodeElements, snapshot, elements, 0);
            }
            catch (Exception ex)
            {
                Log.Write("code model walk failed for " + path, ex);
                return NoTree;
            }

            await TaskScheduler.Default;
            var target = CodeModelTargets.Find(snapshot.GetText(), elements, position, op, count);
            return Answer(snapshot, target);
        }

        private static void Collect(EnvDTE.CodeElements elements, ITextSnapshot snapshot,
                                    System.Collections.Generic.List<CodeModelTargets.Element> into, int depth)
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
            if (elements == null || depth > 32) return;

            foreach (EnvDTE.CodeElement element in elements)
            {
                EnvDTE.vsCMElement kind;
                try { kind = element.Kind; }
                catch { continue; }

                switch (kind)
                {
                    case EnvDTE.vsCMElement.vsCMElementFunction:
                        Add(element, CodeModelTargets.Kind.Function, snapshot, into);
                        break;   // lambdas are not in the code model; nothing to walk into

                    case EnvDTE.vsCMElement.vsCMElementClass:
                    case EnvDTE.vsCMElement.vsCMElementStruct:
                    case EnvDTE.vsCMElement.vsCMElementUnion:
                    case EnvDTE.vsCMElement.vsCMElementInterface:
                    case EnvDTE.vsCMElement.vsCMElementEnum:
                        Add(element, CodeModelTargets.Kind.Type, snapshot, into);
                        TryCollectChildren(element, snapshot, into, depth);
                        break;

                    case EnvDTE.vsCMElement.vsCMElementNamespace:
                        TryCollectChildren(element, snapshot, into, depth);
                        break;
                }
            }
        }

        private static void TryCollectChildren(EnvDTE.CodeElement element, ITextSnapshot snapshot,
                                               System.Collections.Generic.List<CodeModelTargets.Element> into, int depth)
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
            try { Collect(element.Children, snapshot, into, depth + 1); }
            catch { /* an element the code model cannot open: skip it, keep the rest */ }
        }

        private static void Add(EnvDTE.CodeElement element, CodeModelTargets.Kind kind, ITextSnapshot snapshot,
                                System.Collections.Generic.List<CodeModelTargets.Element> into)
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                int start = ToPosition(snapshot, element.GetStartPoint(EnvDTE.vsCMPart.vsCMPartWhole));
                int end = ToPosition(snapshot, element.GetEndPoint(EnvDTE.vsCMPart.vsCMPartWhole));
                if (start >= 0 && end > start) into.Add(new CodeModelTargets.Element(kind, start, end));
            }
            catch { /* no range for this element (a macro-generated one): skip it */ }
        }

        /// <summary>A code-model point (1-based line and character offset) as a snapshot position.</summary>
        private static int ToPosition(ITextSnapshot snapshot, EnvDTE.TextPoint point)
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
            int line = point.Line - 1;
            if (line < 0 || line >= snapshot.LineCount) return -1;
            var snapshotLine = snapshot.GetLineFromLineNumber(line);
            return snapshotLine.Start.Position + Math.Min(Math.Max(point.LineCharOffset - 1, 0), snapshotLine.Length);
        }

        private static object? Answer(ITextSnapshot snapshot, SyntaxTarget? target)
        {
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
