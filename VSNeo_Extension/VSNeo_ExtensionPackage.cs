using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
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
[Guid(PackageGuidString)]
// Nothing here is command-driven, so without an explicit auto-load the package
// is never sited: Session stays null, the key processor sits in pass-through
// forever, and the status bar never says anything at all. BackgroundLoad is what
// keeps that off the UI thread.
[ProvideAutoLoad(VSConstants.UICONTEXT.NoSolution_string, PackageAutoLoadFlags.BackgroundLoad)]
[ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExists_string, PackageAutoLoadFlags.BackgroundLoad)]
[ProvideOptionPage(typeof(VSNeoOptionsPage), "VSNeo", "General", 0, 0, true)]
// VSNeo_Extension.vsct: the Seeky commands and their Ctrl+Shift+Alt chords.
[ProvideMenuResource("Menus.ctmenu", 1)]
// VSNeo.Seeky.dll sits beside this assembly in the extension folder; devenv only probes
// there for it with a binding path.
[ProvideBindingPath]
public sealed partial class VSNeo_ExtensionPackage : AsyncPackage
{
    /// <summary>
    /// VSNeo_ExtensionPackage GUID string.
    /// </summary>
    public const string PackageGuidString = "2213af39-72b4-4827-bda4-da6134c92d0e";
    private static readonly CircuitBreaker Breaker = new CircuitBreaker();
    private NvimSession _session = null!;
    // Cancelled and disposed with the package; passed to every StartAsync so a
    // start still connecting when VS closes retires instead of leaking.
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>
    /// MEF parts reach the session through here. Null until the package has
    /// loaded (the pass-through state), then assigned during InitializeAsync
    /// well before nvim is up: consumers gate on IsReady, not on null.
    /// </summary>
    internal static NvimSession Session { get; private set; } = null!;

    /// <summary>The devenv version ("17.14.5"), cached on the UI thread for diagnostics.</summary>
    internal static string? VisualStudioVersion { get; private set; }

    // For RunVsCommand: MEF listeners and the snap-back logic run VS commands
    // too, and they are not the package. Set alongside Session, cleared on
    // Dispose.
    private static VSNeo_ExtensionPackage? s_instance;

    /// <summary>
    /// Static because a view created before the package loads - and the startup
    /// document is exactly that - has no session instance to subscribe to.
    /// Raised on whatever thread the session reports from; subscribers marshal.
    /// </summary>
    internal static event Action<bool> SessionReadyChanged = null!;

    private IVsRegisterPriorityCommandTarget? _priorityRegistry;   // UI thread only
    private uint _priorityCookie;
    private EnvDTE.DTE? _dte;
    // Cached for the same reason as _dte: split focus moves are keystroke
    // responses, so service resolution must not happen per chord.
    private IVsUIShell? _uiShell;
    private IVsMonitorSelection? _monitorSelection;
    private Editor.SolutionExplorerVim? _solutionExplorerVim;   // UI thread only

    protected override async Task InitializeAsync(
        CancellationToken cancellationToken,
        IProgress<ServiceProgressData> progress)
    {
        Log.Begin("VSNeo package init");

        await base.InitializeAsync(cancellationToken, progress);

        // Everything below is off the UI thread and stays that way.
        _session = new NvimSession(Breaker);
        WireSession(_session);
        VSNeoSettings.Changed += OnSettingsChanged;
        Session = _session;
        s_instance = this;

        // Key binding cleanup does not wait for nvim readiness: until these
        // chords are unbound, Ctrl+E and friends are shell chord prefixes that
        // swallow keystrokes into nothing, and nvim takes seconds to come up.
        // One fire-and-forget UI-thread pass at package load keeps that window
        // at its smallest. Not awaited: nvim startup below proceeds in parallel.
        _ = JoinableTaskFactory.RunAsync(async () =>
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();

            // How long the UI thread goes unresponsive, logged when it passes
            // 100 ms (see Infrastructure.Perf). Needs the UI dispatcher, so it
            // starts here; nothing else waits on it.
            Perf.StartWatchdog(System.Windows.Threading.Dispatcher.CurrentDispatcher);

            // The Seeky chords. First in this pass, so a chord pressed right
            // after startup finds its handler; before registration Visual
            // Studio shows the command disabled and the chord does nothing.
            if (await GetServiceAsync(typeof(System.ComponentModel.Design.IMenuCommandService))
                    is OleMenuCommandService menuCommands)
            {
                Seeky.SeekyCommands.Register(menuCommands);
                VSNeoCommands.Register(menuCommands);
            }

            VSNeo.Seeky.SeekyHost.Current ??= new Seeky.VsNeoSeekyHost();

            // DialogPage settings materialize only when the page is first
            // opened; force that here so a persisted override applies from the
            // start of the session, not from the user's first visit to Options.
            if (GetDialogPage(typeof(VSNeoOptionsPage)) is VSNeoOptionsPage optionsPage)
            {
                optionsPage.PushToStatic();
            }

            // Escape and navigation keys ahead of every view command filter (see KeyPriorityTarget).
            // Registration is an in-memory shell call; no nvim involved.
            if (await GetServiceAsync(typeof(SVsRegisterPriorityCommandTarget))
                    is IVsRegisterPriorityCommandTarget priority
                && ErrorHandler.Succeeded(priority.RegisterPriorityCommandTarget(
                    0, new Editor.KeyPriorityTarget(), out uint cookie)))
            {
                _priorityRegistry = priority;
                _priorityCookie = cookie;
                Log.Write("Escape/navigation priority command target registered");
            }
            else
            {
                Log.Write("Escape/navigation priority command target NOT registered - view filter only");
            }

            _dte = _dte ?? await GetServiceAsync(typeof(SDTE)) as EnvDTE.DTE;
            var dte = _dte;
            if (dte == null)
            {
                // The guard is deliberately not consumed: a session that could
                // not resolve DTE got no cleanup, and a later path may retry.
                Log.Write("key binding cleanup skipped: no DTE");
                return;
            }

            await ApplyKeyBindingsAsync(dte);
        });

        var nvimPath = ResolveNvimPath();
        Log.Write("nvim path: " + nvimPath);

        // Not awaited. InitializeAsync is what a synchronous package load waits
        // on - opening Tools > Options > VSNeo forces one - and nvim's startup
        // can run to the full pipe deadline plus four request timeouts when
        // something is wedged. Awaiting it here made that a UI-thread freeze,
        // the JoinableTaskFactory.Run the invariant forbids, done on our behalf.
        var session = _session;
        _ = JoinableTaskFactory.RunAsync(async () =>
        {
            // StartAsync switches itself to the thread pool first thing.
            await session.StartAsync(nvimPath, _lifetime.Token);
            Log.Write("StartAsync returned, IsReady=" + session.IsReady
                      + (Breaker.LastFault == null ? "" : ", lastFault=" + Breaker.LastFault.Message));
        });
    }

    /// <summary>
    /// The per-session event wiring, shared by the initial start and a retry.
    /// The info handler captures its own session: a retried session must not
    /// answer from the dead one.
    /// </summary>
    private void WireSession(NvimSession session)
    {
        session.ReadyChanged += OnReadyChanged;
        session.ActionRequested += OnActionRequested;
        session.ToggleRequested += OnToggleRequested;
        session.SelectActionRequested += OnSelectActionRequested;
        session.RangeActionRequested += OnRangeActionRequested;
        session.RequestHandler = (method, args) => method switch
        {
            Editor.InfoRequest.Method => Editor.InfoRequest.HandleAsync(session),
            _ => Editor.SyntaxRequests.HandleAsync(method, args),
        };
        session.FocusRequested += OnFocusRequested;
        session.MruRequested += OnMruRequested;
        session.TabJumpRequested += OnTabJumpRequested;
        session.TabJumpPicked += OnTabJumpPicked;
        session.SeekyRequested += OnSeekyRequested;
        session.MirrorStopped += OnMirrorStopped;
    }

    /// <summary>
    /// Posts work to the UI thread at Input priority rather than
    /// SwitchToMainThreadAsync, for the same measured reason as
    /// OnActionRequested: this is the direct response to a keystroke and must
    /// not queue behind Visual Studio's background work.
    /// </summary>
    private static void PostAtInput(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            return;
        }

#pragma warning disable VSTHRD001
        _ = dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Input,
            new Action(() =>
            {
                // The dispatcher guarantees the main thread here, but the analyzer
                // cannot prove it through a BeginInvoke callback.
                ThreadHelper.ThrowIfNotOnUIThread();
                action();
            }));
#pragma warning restore VSTHRD001
    }

    private void OnReadyChanged(bool ready)
    {
        SessionReadyChanged?.Invoke(ready);

        _ = JoinableTaskFactory.RunAsync(async () =>
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            NvimSession session = _session;
            if (await GetServiceAsync(typeof(SVsStatusbar)) is IVsStatusbar bar)
            {
                bar.SetText(!ready
                    ? (session.StartupFailure is string failure
                        ? "VSNeo: " + failure
                        : "VSNeo: fallback (VS input)")
                    : VSNeoSettings.Enabled ? "VSNeo: connected" : "VSNeo: disabled (VS input)");
            }

            // The permanent mode indicator has the opposite lifetime: attached
            // when there is a mode worth showing, hidden while input is VS's.
            if (ready)
            {
                Editor.ModeStatusBarItem.Attach(session);
            }
            else
            {
                Editor.ModeStatusBarItem.Hide();
            }

            // The shell-owned cmdline overlay subscribes once the session (and
            // with it the hub state it renders) actually exists.
            // Gone, it hides: the hub never clears CmdLine on disconnect, so a
            // ':' open when nvim died would otherwise float for the session.
            if (ready)
            {
                Editor.CmdLineOverlayWindow.Attach(session);
            }
            else
            {
                Editor.CmdLineOverlayWindow.Hide();
            }

            // Cached because every Vim mapping bound to a VS command needs it, and
            // resolving a service per keystroke is work the key path should not do.
            // (_dte is usually already set by the binding-cleanup pass kicked off in
            // InitializeAsync; this is the fallback for a session that never cleaned.)
            _dte = _dte ?? await GetServiceAsync(typeof(SDTE)) as EnvDTE.DTE;
            if (_dte != null)
            {
                VisualStudioVersion ??= _dte.Version;
            }

            _uiShell = _uiShell ?? await GetServiceAsync(typeof(SVsUIShell)) as IVsUIShell;
            _monitorSelection = _monitorSelection
                ?? await GetServiceAsync(typeof(SVsShellMonitorSelection)) as IVsMonitorSelection;
            if (_solutionExplorerVim == null && _dte != null && _monitorSelection != null)
            {
                var navigation = new Editor.SolutionExplorerVim();
                if (navigation.Attach(_dte, _monitorSelection))
                {
                    _solutionExplorerVim = navigation;
                }
            }

            if (ready && _dte != null)
            {
                await RefreshVsCommandsAsync();
            }
        });
    }

    protected override void Dispose(bool disposing)
    {
        // The shell calls Dispose on the main thread; Detach asserts it.
        ThreadHelper.ThrowIfNotOnUIThread();

        if (disposing)
        {
            try
            { _lifetime.Cancel(); }
            catch (ObjectDisposedException) { }

            VSNeoSettings.Changed -= OnSettingsChanged;
            if (_priorityRegistry != null)
            {
                _priorityRegistry.UnregisterPriorityCommandTarget(_priorityCookie);
                _priorityRegistry = null;
            }

            Editor.CmdLineOverlayWindow.Detach();
            Editor.ModeStatusBarItem.Detach();
            _solutionExplorerVim?.Detach();
            VSNeo.Seeky.SeekyPickerController.Shutdown();

            // Back to pass-through: consumers read Session as non-nullable and
            // branch on the null, so the property type stays as it is.
            Session = null!;
            s_instance = null;
            _session?.Dispose();

            // The log drainer is a background thread and dies with the process;
            // flush so the shutdown lines survive.
            Log.Flush();
            _lifetime.Dispose();
        }

        base.Dispose(disposing);
    }
}
