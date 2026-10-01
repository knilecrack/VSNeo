using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Windows.Threading;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using VSNeo_Extension.Infrastructure;

namespace VSNeo_Extension.Editor
{
    /// <summary>
    /// TEMPORARY probe: which languages publish typed structure tags
    /// (IStructureTag.Type "Member" / "Type") - the hook that would give
    /// TypeScript and others exact af/ac/]m/]] with no treesitter parser.
    /// Two seconds after a document first gets focus (language services fill
    /// structure in asynchronously), logs one line per file:
    ///   structure tags for app.ts [TypeScript]: Member 14, Type 3, ...
    /// Remove once the answer is known.
    /// </summary>
    [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class StructureTagProbe : IWpfTextViewCreationListener
    {
        [Import]
        internal IViewTagAggregatorFactoryService TagAggregators { get; set; } = null!;

        private static readonly HashSet<string> Logged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public void TextViewCreated(IWpfTextView textView)
        {
            // Bookkeeping only (see TextViewCreationListener).
            textView.GotAggregateFocus += OnGotFocus;
            textView.Closed += (s, e) => textView.GotAggregateFocus -= OnGotFocus;
        }

        private void OnGotFocus(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var view = (IWpfTextView)sender;
            var name = NameOf(view);
            if (name == null || Logged.Contains(name)) return;
            Logged.Add(name);

            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (s, a) =>
            {
                timer.Stop();
                if (!view.IsClosed) Probe(view, name);
            };
            timer.Start();
        }

        private void Probe(IWpfTextView view, string name)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var snapshot = view.TextSnapshot;
                using var aggregator = TagAggregators.CreateTagAggregator<IStructureTag>(view);
                var counts = aggregator
                    .GetTags(new SnapshotSpan(snapshot, 0, snapshot.Length))
                    .GroupBy(t => string.IsNullOrEmpty(t.Tag.Type) ? "(untyped)" : t.Tag.Type)
                    .OrderByDescending(g => g.Count())
                    .Select(g => g.Key + " " + g.Count());
                var summary = string.Join(", ", counts);
                Log.Write("structure tags for " + name + " [" + view.TextBuffer.ContentType.TypeName + "]: "
                          + (summary.Length == 0 ? "none" : summary));
            }
            catch (Exception ex)
            {
                Log.Write("structure tag probe failed for " + name, ex);
            }
        }

        private static string? NameOf(IWpfTextView view)
        {
            return view.TextBuffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument document)
                ? System.IO.Path.GetFileName(document.FilePath)
                : null;
        }
    }
}
