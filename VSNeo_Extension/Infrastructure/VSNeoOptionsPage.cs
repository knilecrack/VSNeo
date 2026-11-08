using System;
using System.ComponentModel;
using Microsoft.VisualStudio.Shell;

namespace VSNeo_Extension.Infrastructure
{
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

        /// <summary>Raised on the thread that applied the change (the UI thread for OnApply).</summary>
        public static event Action Changed = null!;

        internal static void Apply(RelativeLineNumbersMode mode, bool enabled)
        {
            bool changed = RelativeLineNumbers != mode || Enabled != enabled;
            if (!changed) return;
            RelativeLineNumbers = mode;
            System.Threading.Volatile.Write(ref _enabled, enabled);
            Changed?.Invoke();
        }

        internal static void SetEnabled(bool enabled) => Apply(RelativeLineNumbers, enabled);
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
            "FollowNeovim (default) draws the margin while Neovim's 'relativenumber' is on, " +
            "with an absolute cursor-line number when 'number' is on too. " +
            "AlwaysOn forces the relative margin on but keeps the cursor-line number tied to 'number'; " +
            "AlwaysOff restores Visual Studio's native margin. Hybrid forces the relative margin on " +
            "with an absolute cursor-line number and relative numbers elsewhere. Overrides do not " +
            "change Neovim options.")]
        public RelativeLineNumbersMode RelativeLineNumbers { get; set; }
            = RelativeLineNumbersMode.FollowNeovim;

        protected override void OnApply(PageApplyEventArgs e)
        {
            base.OnApply(e);
            VSNeoSettings.Apply(RelativeLineNumbers, Enabled);
        }

        /// <summary>
        /// Pushes the persisted value into <see cref="VSNeoSettings"/> without
        /// waiting for the user to open the page. Called once at package load.
        /// </summary>
        internal void PushToStatic() => VSNeoSettings.Apply(RelativeLineNumbers, Enabled);
    }
}
