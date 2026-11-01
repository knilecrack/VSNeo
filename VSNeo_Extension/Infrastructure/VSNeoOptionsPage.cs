using System;
using System.ComponentModel;
using Microsoft.VisualStudio.Shell;

namespace VSNeo_Extension.Infrastructure
{
    /// <summary>How the relative line number margin decides whether to draw.</summary>
    public enum RelativeLineNumbersMode
    {
        /// <summary>Follow nvim's 'relativenumber' option; ':set rnu' toggles the margin live.</summary>
        FollowNeovim = 0,
        AlwaysOn = 1,
        AlwaysOff = 2,
    }

    /// <summary>
    /// The live Tools &gt; Options values, readable without a package or a UI
    /// context. DialogPage only materializes when the page is first opened, so
    /// the package pushes the persisted values in at load (see
    /// VSNeo_ExtensionPackage.InitializeAsync); OnApply keeps them current
    /// after that.
    /// </summary>
    internal static class VSNeoSettings
    {
        public static RelativeLineNumbersMode RelativeLineNumbers { get; private set; }
            = RelativeLineNumbersMode.FollowNeovim;

        private static bool _enabled = true;
        public static bool Enabled => System.Threading.Volatile.Read(ref _enabled);

        private static bool _solutionExplorerVim;
        public static bool SolutionExplorerVim => System.Threading.Volatile.Read(ref _solutionExplorerVim);

        /// <summary>Raised on the thread that applied the change (the UI thread for OnApply).</summary>
        public static event Action Changed = null!;

        internal static void Apply(RelativeLineNumbersMode mode, bool enabled, bool solutionExplorerVim)
        {
            bool changed = RelativeLineNumbers != mode || Enabled != enabled
                || SolutionExplorerVim != solutionExplorerVim;
            if (!changed) return;
            RelativeLineNumbers = mode;
            System.Threading.Volatile.Write(ref _enabled, enabled);
            System.Threading.Volatile.Write(ref _solutionExplorerVim, solutionExplorerVim);
            Changed?.Invoke();
        }

        internal static void SetEnabled(bool enabled) => Apply(RelativeLineNumbers, enabled, SolutionExplorerVim);
    }

    public sealed class VSNeoOptionsPage : DialogPage
    {
        [Category("General")]
        [DisplayName("Enable VSNeo")]
        [Description("Route editor input through Neovim. Disable to use plain Visual Studio editing without unloading VSNeo.")]
        public bool Enabled { get; set; } = true;

        [Category("Editor")]
        [DisplayName("Relative line numbers")]
        [Description(
            "Vim-style relative line numbers in the editor margin. Follow Neovim honors " +
            "'relativenumber' from ~/.vsneorc and live ':set rnu' toggles; the other two " +
            "settings override it.")]
        public RelativeLineNumbersMode RelativeLineNumbers { get; set; }
            = RelativeLineNumbersMode.FollowNeovim;

        [Category("Solution Explorer")]
        [DisplayName("Vim navigation")]
        [Description("Use j/k, gg/G, h/l, o/Enter and q in Solution Explorer. Text search and inline editing keep normal Visual Studio input. Off by default.")]
        public bool SolutionExplorerVim { get; set; }

        protected override void OnApply(PageApplyEventArgs e)
        {
            base.OnApply(e);
            VSNeoSettings.Apply(RelativeLineNumbers, Enabled, SolutionExplorerVim);
        }

        /// <summary>
        /// Pushes the persisted value into <see cref="VSNeoSettings"/> without
        /// waiting for the user to open the page. Called once at package load.
        /// </summary>
        internal void PushToStatic() => VSNeoSettings.Apply(RelativeLineNumbers, Enabled, SolutionExplorerVim);
    }
}
