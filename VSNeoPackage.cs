using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using VSNeo.Infrastructure;
using VSNeo.Nvim;
using Task = System.Threading.Tasks.Task;

namespace VSNeo
{
    /// <summary>
    /// Owns the embedded nvim for this Visual Studio instance.
    ///
    /// AllowsBackgroundLoading plus PackageAutoLoadFlags.BackgroundLoad is what
    /// keeps startup off the UI thread. If you ever find yourself reaching for
    /// JoinableTaskFactory.Run or .Result in here, that is the freeze coming back.
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(PackageGuidString)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.NoSolution_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExists_string, PackageAutoLoadFlags.BackgroundLoad)]
    public sealed class VSNeoPackage : AsyncPackage
    {
        public const string PackageGuidString = "6f2b9a31-1c4e-4e2b-9e3d-9b1a5a7c0002";

        private static readonly CircuitBreaker Breaker = new CircuitBreaker();
        private NvimSession _session;

        /// <summary>
        /// MEF parts reach the session through here. Null until activation
        /// completes, which is exactly the pass-through state we want.
        /// </summary>
        internal static NvimSession Session { get; private set; }

        protected override async Task InitializeAsync(
            CancellationToken cancellationToken,
            IProgress<ServiceProgressData> progress)
        {
            await base.InitializeAsync(cancellationToken, progress);

            // Everything below is off the UI thread and stays that way.
            _session = new NvimSession(Breaker);
            _session.ReadyChanged += OnReadyChanged;
            Session = _session;

            var nvimPath = Environment.GetEnvironmentVariable("VSNEO_NVIM_PATH") ?? "nvim.exe";
            await _session.StartAsync(nvimPath, cancellationToken);
        }

        private void OnReadyChanged(bool ready)
        {
            _ = JoinableTaskFactory.RunAsync(async () =>
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                if (await GetServiceAsync(typeof(SVsStatusbar)) is IVsStatusbar bar)
                    bar.SetText(ready ? "VSNeo: connected" : "VSNeo: fallback (VS input)");
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Session = null;
                _session?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
