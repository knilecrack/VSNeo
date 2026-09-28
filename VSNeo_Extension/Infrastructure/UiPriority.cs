using System.Windows.Threading;

namespace VSNeo_Extension.Infrastructure
{
    /// <summary>
    /// Dispatcher priorities for work posted to the UI thread from the RPC reader.
    ///
    /// WPF runs higher priorities first: Send, Normal, DataBind, Render, Loaded,
    /// Input, Background. Input sits *below* Render and Normal, so an item posted
    /// there waits for every layout pass, every repaint and all of Visual Studio's
    /// own Normal-priority work (Roslyn, CodeLens, taggers) queued ahead of it.
    /// Measured with Perf: key->caret p50 25-60 ms, p95 up to 185 ms, and the
    /// UI-thread share ("cursor hop") as large as the whole - the caret was not
    /// slow to move, it was waiting in line.
    /// </summary>
    internal static class UiPriority
    {
        /// <summary>
        /// The direct, visible effect of a keystroke: the caret landing, nvim's
        /// scroll, an operator's edit. Ahead of everything, including rendering,
        /// so the next frame already shows it. Handlers posted here must stay
        /// cheap (they coalesce: one pending item per burst) and must not run
        /// Visual Studio commands, which can pump or open dialogs.
        /// </summary>
        public const DispatcherPriority KeyResponse = DispatcherPriority.Send;

        /// <summary>
        /// Decoration that follows a keystroke - trail, mode tint, line numbers,
        /// highlights, popups - and Visual Studio commands run from mappings.
        /// Still ahead of Visual Studio's background backlog, but behind the
        /// caret and behind rendering.
        /// </summary>
        public const DispatcherPriority Decoration = DispatcherPriority.Input;
    }
}
