using System;
using System.IO;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace VSNeo_Extension.Infrastructure
{
    /// <summary>
    /// A shada file per solution. nvim starts with -i NONE because every
    /// Visual Studio instance runs its own nvim and a shared shada leaks
    /// registers, marks and history between them - but that also meant marks,
    /// the jumplist, registers and command history died with the session.
    /// Keyed on the solution, each instance gets its own memory and two
    /// instances on different solutions no longer collide: &lt;solution
    /// dir&gt;\.vs\vsneo.shada, next to Visual Studio's own per-solution state
    /// (.vs is ignored by the usual .gitignore).
    ///
    /// Watches solution open/close; the package hands the path to the
    /// companion (vsneo.set_shada), which switches 'shadafile', reads it,
    /// and writes it back on a timer and on close, since nvim is killed at
    /// shutdown and never gets to write it itself.
    /// </summary>
    internal sealed class SolutionShada : IVsSolutionEvents, IDisposable
    {
        public const string FileName = "vsneo.shada";

        private readonly Action<string?> _onSolutionChanged;
        private IVsSolution? _solution;
        private uint _cookie;

        public SolutionShada(Action<string?> onSolutionChanged)
        {
            _onSolutionChanged = onSolutionChanged;
        }

        /// <summary>The shada path for the open solution, or null when none is open.</summary>
        public string? CurrentPath { get; private set; }

        /// <summary>UI thread. Subscribes, and reports a solution that is already open.</summary>
        public void Advise(IVsSolution solution)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _solution = solution;
            if (ErrorHandler.Failed(solution.AdviseSolutionEvents(this, out _cookie)))
            {
                Log.Write("shada: could not subscribe to solution events");
                return;
            }
            Report(PathForOpenSolution());
        }

        private string? PathForOpenSolution()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (_solution == null) return null;
                if (ErrorHandler.Failed(_solution.GetProperty((int)__VSPROPID.VSPROPID_IsSolutionOpen, out object open))
                    || !(open is bool isOpen) || !isOpen)
                    return null;
                if (ErrorHandler.Failed(_solution.GetProperty((int)__VSPROPID.VSPROPID_SolutionDirectory, out object dir))
                    || !(dir is string directory) || directory.Length == 0)
                    return null;
                return Path.Combine(directory, ".vs", FileName);
            }
            catch (Exception ex)
            {
                Log.Write("shada: could not resolve the solution directory", ex);
                return null;
            }
        }

        private void Report(string? path)
        {
            if (string.Equals(path, CurrentPath, StringComparison.OrdinalIgnoreCase)) return;
            CurrentPath = path;
            Log.Write("shada: " + (path ?? "none (no solution open)"));
            try { _onSolutionChanged(path); }
            catch (Exception ex) { Log.Write("shada: handler threw", ex); }
        }

        public int OnAfterOpenSolution(object pUnkReserved, int fNewSolution)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Report(PathForOpenSolution());
            return VSConstants.S_OK;
        }

        public int OnBeforeCloseSolution(object pUnkReserved)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Report(null);
            return VSConstants.S_OK;
        }

        public void Dispose()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_solution != null && _cookie != 0)
            {
                try { _solution.UnadviseSolutionEvents(_cookie); } catch { }
                _cookie = 0;
            }
        }

        // The rest of IVsSolutionEvents: projects, not solutions.
        public int OnAfterOpenProject(IVsHierarchy pHierarchy, int fAdded) => VSConstants.S_OK;
        public int OnQueryCloseProject(IVsHierarchy pHierarchy, int fRemoving, ref int pfCancel) => VSConstants.S_OK;
        public int OnBeforeCloseProject(IVsHierarchy pHierarchy, int fRemoved) => VSConstants.S_OK;
        public int OnAfterLoadProject(IVsHierarchy pStubHierarchy, IVsHierarchy pRealHierarchy) => VSConstants.S_OK;
        public int OnQueryUnloadProject(IVsHierarchy pRealHierarchy, ref int pfCancel) => VSConstants.S_OK;
        public int OnBeforeUnloadProject(IVsHierarchy pRealHierarchy, IVsHierarchy pStubHierarchy) => VSConstants.S_OK;
        public int OnQueryCloseSolution(object pUnkReserved, ref int pfCancel) => VSConstants.S_OK;
        public int OnAfterCloseSolution(object pUnkReserved) => VSConstants.S_OK;
    }
}
