using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using VSNeo_Extension.Infrastructure;

namespace VSNeo_Extension.Editor
{
    /// <summary>Option-gated Vim motions for the focused Solution Explorer.</summary>
    internal sealed class SolutionExplorerVim
    {
        private DTE? _dte;
        private UIHierarchy? _hierarchy;
        private IVsUIHierarchyWindow? _hierarchyWindow;
        private IVsMonitorSelection? _monitorSelection;
        private bool _attached;
        private int _pendingGUntil;

        internal bool Attach(DTE dte, IVsMonitorSelection monitorSelection)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_attached) return true;

            // Solution Explorer itself is resolved on the first key it receives: at package
            // load it often does not exist yet, and an attach that required it never retried.
            _dte = dte;
            _monitorSelection = monitorSelection;
            InputManager.Current.PreProcessInput += OnPreProcessInput;
            _attached = true;
            return true;
        }

        private void Resolve()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_hierarchy != null && _hierarchyWindow != null) return;
            try
            {
                _hierarchy ??= _dte?.Windows.Item(EnvDTE.Constants.vsWindowKindSolutionExplorer).Object as UIHierarchy;
                _hierarchyWindow ??= VsShellUtilities.GetUIHierarchyWindow(
                    ServiceProvider.GlobalProvider, VSConstants.StandardToolWindows.SolutionExplorer);
            }
            catch (COMException ex)
            {
                Log.Write("solution explorer vim: resolving Solution Explorer failed", ex);
            }
            if (_hierarchy == null || _hierarchyWindow == null)
                Log.Write($"solution explorer vim: unresolved (hierarchy {_hierarchy != null}, window {_hierarchyWindow != null})");
        }

        internal void Detach()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!_attached) return;
            InputManager.Current.PreProcessInput -= OnPreProcessInput;
            _attached = false;
            _dte = null;
            _hierarchy = null;
            _hierarchyWindow = null;
            _monitorSelection = null;
        }

        private void OnPreProcessInput(object sender, PreProcessInputEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!VSNeoSettings.Enabled || !VSNeoSettings.SolutionExplorerVim) return;
            if (e.StagingItem.Input is not KeyEventArgs key || key.Handled
                || key.RoutedEvent != Keyboard.KeyDownEvent || key.Key == Key.System
                || key.Key == Key.ImeProcessed)
                return;

            var modifiers = Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt);
            if (modifiers != ModifierKeys.None || IsTextInput(Keyboard.FocusedElement) || !IsActive())
                return;

            var mapped = MapKey(key.Key, (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
            if (mapped == SolutionExplorerVimKey.None) return;
            Resolve();
            var action = SolutionExplorerVimKeys.Next(ref _pendingGUntil, mapped, Environment.TickCount);
            if (Execute(action)) key.Handled = true;
            else Log.Write($"solution explorer vim: {mapped} -> {action} not handled");
        }

        private static bool IsTextInput(IInputElement? focused) =>
            focused is TextBoxBase || focused is PasswordBox || focused is ComboBox;

        private bool IsActive()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var dte = _dte;
            if (dte == null) return false;
            // Not HWnd: WPF-hosted windows report 0 for it, so every window - the editor
            // included - compared equal and the first g of gg was swallowed here.
            try
            {
                var active = dte.ActiveWindow;
                return active != null && (active.Type == vsWindowType.vsWindowTypeSolutionExplorer
                    || string.Equals(active.ObjectKind, EnvDTE.Constants.vsWindowKindSolutionExplorer,
                        StringComparison.OrdinalIgnoreCase));
            }
            catch (COMException) { return false; }
        }

        private static SolutionExplorerVimKey MapKey(Key key, bool shift)
        {
            if (shift) return key == Key.G ? SolutionExplorerVimKey.ShiftG : SolutionExplorerVimKey.None;
            switch (key)
            {
                case Key.J: return SolutionExplorerVimKey.J;
                case Key.K: return SolutionExplorerVimKey.K;
                case Key.G: return SolutionExplorerVimKey.G;
                case Key.H: return SolutionExplorerVimKey.H;
                case Key.L: return SolutionExplorerVimKey.L;
                case Key.O: return SolutionExplorerVimKey.O;
                case Key.Q: return SolutionExplorerVimKey.Q;
                case Key.Return: return SolutionExplorerVimKey.Enter;
                default: return SolutionExplorerVimKey.None;
            }
        }

        private bool Execute(SolutionExplorerVimAction action)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (action == SolutionExplorerVimAction.Wait) return true;
            if (action == SolutionExplorerVimAction.None) return false;
            // DTE's UIHierarchy is the solution's tree only: in Folder view it reports no
            // selection and every call fails. The tree itself handles the arrow keys in both.
            return ExecuteViaDte(action) || SendKey(FallbackKey(action));
        }

        private static Key FallbackKey(SolutionExplorerVimAction action) => action switch
        {
            SolutionExplorerVimAction.Up => Key.Up,
            SolutionExplorerVimAction.Down => Key.Down,
            SolutionExplorerVimAction.Top => Key.Home,
            SolutionExplorerVimAction.Bottom => Key.End,
            SolutionExplorerVimAction.Collapse => Key.Left,
            SolutionExplorerVimAction.Expand => Key.Right,
            SolutionExplorerVimAction.Open => Key.Return,
            _ => Key.None,
        };

        /// <summary>Raises <paramref name="key"/> on the focused element, Preview then bubble,
        /// as a real press would arrive. Never through the InputManager, so it never re-enters
        /// <see cref="OnPreProcessInput"/>.</summary>
        private static bool SendKey(Key key)
        {
            if (key == Key.None || Keyboard.FocusedElement is not UIElement target) return false;
            var source = PresentationSource.FromVisual(target);
            if (source == null) return false;
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
            };
            target.RaiseEvent(args);
            if (!args.Handled)
            {
                args.RoutedEvent = Keyboard.KeyDownEvent;
                target.RaiseEvent(args);
            }
            return args.Handled;
        }

        private bool ExecuteViaDte(SolutionExplorerVimAction action)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                switch (action)
                {
                    case SolutionExplorerVimAction.Up: return Move(up: true, 1);
                    case SolutionExplorerVimAction.Down: return Move(up: false, 1);
                    case SolutionExplorerVimAction.Top: return Move(up: true, int.MaxValue);
                    case SolutionExplorerVimAction.Bottom: return Move(up: false, int.MaxValue);
                    case SolutionExplorerVimAction.Collapse: return SetExpansion(EXPANDFLAGS.EXPF_CollapseFolder);
                    case SolutionExplorerVimAction.Expand: return SetExpansion(EXPANDFLAGS.EXPF_ExpandFolder);
                    case SolutionExplorerVimAction.Open:
                        if (!HasSelection()) return false;
                        _hierarchy!.DoDefaultAction();
                        return true;
                    case SolutionExplorerVimAction.ReturnToEditor:
                        var document = _dte?.ActiveDocument;
                        if (document == null) return false;
                        document.Activate();
                        return true;
                    default: return false;
                }
            }
            catch (COMException)
            {
                return false;
            }
            catch (NullReferenceException)
            {
                // VS's UIHierarchy marshaler throws when its native selection disappears.
                return false;
            }
        }

        private bool HasSelection()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return _hierarchy?.SelectedItems is Array selected && selected.Length > 0;
        }

        private bool Move(bool up, int count)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!HasSelection()) return false;
            if (up) _hierarchy!.SelectUp(vsUISelectionType.vsUISelectionTypeSelect, count);
            else _hierarchy!.SelectDown(vsUISelectionType.vsUISelectionTypeSelect, count);
            return true;
        }

        private bool SetExpansion(EXPANDFLAGS flags)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var window = _hierarchyWindow;
            var monitor = _monitorSelection;
            if (window == null || monitor == null) return false;

            IntPtr hierarchyPointer = IntPtr.Zero;
            IntPtr selectionContainer = IntPtr.Zero;
            try
            {
                if (ErrorHandler.Failed(monitor.GetCurrentSelection(
                        out hierarchyPointer, out uint itemId, out _, out selectionContainer))
                    || hierarchyPointer == IntPtr.Zero)
                    return false;
                if (Marshal.GetObjectForIUnknown(hierarchyPointer) is not IVsUIHierarchy hierarchy)
                    return false;
                return ErrorHandler.Succeeded(window.ExpandItem(hierarchy, itemId, flags));
            }
            finally
            {
                if (hierarchyPointer != IntPtr.Zero) Marshal.Release(hierarchyPointer);
                if (selectionContainer != IntPtr.Zero) Marshal.Release(selectionContainer);
            }
        }
    }
}
