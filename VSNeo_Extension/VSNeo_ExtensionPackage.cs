using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using VSNeo_Extension.Infrastructure;
using VSNeo_Extension.Nvim;

namespace VSNeo_Extension;
/// <summary>
/// This is the class that implements the package exposed by this assembly.
/// </summary>
/// <remarks>
/// <para>
/// The minimum requirement for a class to be considered a valid package for Visual Studio
/// is to implement the IVsPackage interface and register itself with the shell.
/// This package uses the helper classes defined inside the Managed Package Framework (MPF)
/// to do it: it derives from the Package class that provides the implementation of the
/// IVsPackage interface and uses the registration attributes defined in the framework to
/// register itself and its components with the shell. These attributes tell the pkgdef creation
/// utility what data to put into .pkgdef file.
/// </para>
/// <para>
/// To get loaded into VS, the package must be referred by &lt;Asset Type="Microsoft.VisualStudio.VsPackage" ...&gt; in .vsixmanifest file.
/// </para>
/// </remarks>
[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[Guid(VSNeo_ExtensionPackage.PackageGuidString)]
// Nothing here is command-driven, so without an explicit auto-load the package
// is never sited: Session stays null, the key processor sits in pass-through
// forever, and the status bar never says anything at all. BackgroundLoad is what
// keeps that off the UI thread.
[ProvideAutoLoad(VSConstants.UICONTEXT.NoSolution_string, PackageAutoLoadFlags.BackgroundLoad)]
[ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExists_string, PackageAutoLoadFlags.BackgroundLoad)]
public sealed class VSNeo_ExtensionPackage : AsyncPackage
{
    /// <summary>
    /// VSNeo_ExtensionPackage GUID string.
    /// </summary>
    public const string PackageGuidString = "2213af39-72b4-4827-bda4-da6134c92d0e";
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
        Log.Begin("VSNeo package init");

        await base.InitializeAsync(cancellationToken, progress);

        // Everything below is off the UI thread and stays that way.
        _session = new NvimSession(Breaker);
        _session.ReadyChanged += OnReadyChanged;
        Session = _session;

        var nvimPath = Environment.GetEnvironmentVariable("VSNEO_NVIM_PATH") ?? "nvim.exe";
        Log.Write("nvim path: " + nvimPath);

        await _session.StartAsync(nvimPath, cancellationToken);

        Log.Write("StartAsync returned, IsReady=" + _session.IsReady
                  + (Breaker.LastFault == null ? "" : ", lastFault=" + Breaker.LastFault.Message));
    }

    private int _bindingsCleaned;

    private void OnReadyChanged(bool ready)
    {
        _ = JoinableTaskFactory.RunAsync(async () =>
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            if (await GetServiceAsync(typeof(SVsStatusbar)) is IVsStatusbar bar)
                bar.SetText(ready ? "VSNeo: connected" : "VSNeo: fallback (VS input)");

            // Deliberately here rather than in InitializeAsync. Walking every command
            // in the shell is not something to put on the startup path - eager work
            // there froze Visual Studio once already - and it is pointless until
            // there is an nvim to hand the keys to. Once per session is enough:
            // the removals persist in the user's configuration.
            if (ready && Interlocked.Exchange(ref _bindingsCleaned, 1) == 0)
                Infrastructure.KeyBindingCleaner.Run(
                    await GetServiceAsync(typeof(SDTE)) as EnvDTE.DTE);
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


    #region Package Members

    ///// <summary>
    ///// Initialization of the package; this method is called right after the package is sited, so this is the place
    ///// where you can put all the initialization code that rely on services provided by VisualStudio.
    ///// </summary>
    ///// <param name="cancellationToken">A cancellation token to monitor for initialization cancellation, which can occur when VS is shutting down.</param>
    ///// <param name="progress">A provider for progress updates.</param>
    ///// <returns>A task representing the async work of package initialization, or an already completed task if there is none. Do not return null from this method.</returns>
    //protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    //{
    //    // When initialized asynchronously, the current thread may be a background thread at this point.
    //    // Do any initialization that requires the UI thread after switching to the UI thread.
    //    await this.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
    //}

    #endregion
}
