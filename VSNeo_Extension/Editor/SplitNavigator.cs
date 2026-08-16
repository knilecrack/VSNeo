using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using VSNeo_Extension.Infrastructure;

namespace VSNeo_Extension.Editor
{
    /// <summary>
    /// Directional focus movement between document tab groups, for Ctrl-W h/j/k/l.
    ///
    /// Visual Studio has no directional "go left" between splits - only next /
    /// previous cycling - so direction is resolved from frame geometry instead: the
    /// on-screen document frames sit side by side (vertical groups) or stacked
    /// (horizontal groups), and the nearest neighbor that overlaps the active frame
    /// on the orthogonal axis wins.
    ///
    /// Which tab of the target group gets focused is the harder half. The shell
    /// exposes no "selected tab of a group" API - IsOnScreen and IsVisible both
    /// report every tab (PeasyMotion's source calls this out too), and Show() on a
    /// non-selected tab silently switches the group to that file. So this class
    /// remembers every document frame it sees focused - fed deterministically by
    /// the focus listener through IVsTextViewEx.GetWindowFrame - and breaks the
    /// geometry tie between a group's tabs in favor of the one used most recently.
    ///
    /// UI thread only. Every failure degrades to a no-op: a keystroke must never
    /// fault, and a split that cannot be found simply does nothing.
    /// </summary>
    internal static class SplitNavigator
    {
        // Adjacent groups share an edge in theory and are a pixel or two apart in
        // practice; screen coordinates are not exact enough to demand equality.
        private const int EdgeTolerance = 4;

        // Document frames that have been focused at some point, oldest first.
        // Closed frames never match a live enumeration, so staleness is harmless;
        // the cap just bounds memory.
        private const int RememberedCapacity = 64;
        private static readonly List<IVsWindowFrame> Remembered = new List<IVsWindowFrame>();

        private struct FrameRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;

            public override string ToString() =>
                $"[{Left},{Top} - {Right},{Bottom}]";
        }

        /// <summary>
        /// Records the frame hosting the text view that just got focus as the most
        /// recently used tab of its group. IVsTextViewEx.GetWindowFrame is a direct
        /// view-to-frame lookup (VsVim/PeasyMotion use it the same way), so unlike
        /// the shell's selection element this is exact and has no timing window.
        /// </summary>
        public static void RememberViewFrame(
            IWpfTextView view,
            IVsEditorAdaptersFactoryService editorAdapters)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                var adapter = editorAdapters.GetViewAdapter(view);
                if (adapter is not IVsTextViewEx textViewEx) return;
                if (ErrorHandler.Failed(textViewEx.GetWindowFrame(out var frameObj))) return;
                if (frameObj is not IVsWindowFrame frame) return;
                Remember(frame);
            }
            catch (Exception ex)
            {
                Log.Write("could not remember the focused document frame", ex);
            }
        }

        public static void MoveFocus(
            string direction,
            IVsUIShell? uiShell,
            IVsMonitorSelection? monitorSelection)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (uiShell == null || monitorSelection == null) return;

            try
            {
                if (!TryGetActiveFrame(monitorSelection, out var activeFrame, out var active))
                    return;

                // The frame the chord was pressed from is by definition its group's
                // selected tab; remembering it here keeps the memory warm even if
                // the focus listener missed a transition.
                Remember(activeFrame);

                Log.Write("split focus " + direction
                          + ": active='" + CaptionOf(activeFrame) + "' " + active);

                IVsWindowFrame? best = null;
                var bestDistance = int.MaxValue;
                var bestOverlap = -1;
                var bestRank = -1;

                foreach (var (frame, rect) in EnumerateVisibleFrames(uiShell))
                {
                    if (IsSameObject(frame, activeFrame)) continue;
                    if (!TryScore(direction, active, rect, out var distance, out var overlap))
                        continue;

                    // Tabs of one group share a rectangle, so distance and overlap
                    // tie; the most recently used tab of the group wins the tie.
                    var rank = RecencyRank(frame);
                    Log.Write("  candidate '" + CaptionOf(frame) + "' " + rect
                              + " dist=" + distance + " overlap=" + overlap + " rank=" + rank);

                    if (distance < bestDistance
                        || (distance == bestDistance && overlap > bestOverlap)
                        || (distance == bestDistance && overlap == bestOverlap && rank > bestRank))
                    {
                        best = frame;
                        bestDistance = distance;
                        bestOverlap = overlap;
                        bestRank = rank;
                    }
                }

                Log.Write(best == null
                    ? "  -> no candidate"
                    : "  -> showing '" + CaptionOf(best) + "'");
                best?.Show();
            }
            catch (Exception ex)
            {
                Log.Write("split focus " + direction + " failed", ex);
            }
        }

        // MRU walk state. The walk goes over a snapshot: every landing fires a
        // focus event that reorders the live memory, and walking the live list
        // would ping-pong between the two most recent documents. The snapshot
        // is rebuilt once the pause between presses exceeds the timeout - the
        // Ctrl+Tab "commit" moment.
        private static readonly List<IVsWindowFrame> Traversal = new List<IVsWindowFrame>();
        private static DateTime _lastCycleUtc = DateTime.MinValue;
        private static readonly TimeSpan TraversalTimeout = TimeSpan.FromSeconds(2);

        /// <summary>
        /// Steps one document back in most-recently-used order. A single press is
        /// the alternate-file toggle; pressing again within TraversalTimeout
        /// walks further back through the MRU history.
        /// </summary>
        public static void CycleBack(IVsMonitorSelection? monitorSelection)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (monitorSelection == null) return;

            try
            {
                var now = DateTime.UtcNow;
                if (now - _lastCycleUtc > TraversalTimeout)
                {
                    Traversal.Clear();
                    // Remembered is oldest-first, so reverse into newest-first.
                    for (var i = Remembered.Count - 1; i >= 0; i--)
                        Traversal.Add(Remembered[i]);
                }
                _lastCycleUtc = now;
                if (Traversal.Count == 0) return;

                TryGetActiveFrame(monitorSelection, out var activeFrame, out _);

                var startIndex = -1;
                if (activeFrame != null)
                {
                    for (var i = 0; i < Traversal.Count; i++)
                    {
                        if (IsSameObject(Traversal[i], activeFrame))
                        {
                            startIndex = i;
                            break;
                        }
                    }
                }

                for (var step = 1; step <= Traversal.Count; step++)
                {
                    var frame = Traversal[(startIndex + step) % Traversal.Count];
                    try
                    {
                        // IsOnScreen doubles as the liveness check here: every
                        // live tab reports on screen (which is exactly why it
                        // cannot pick a group's selected tab), so a failure or
                        // false means the snapshot entry died.
                        if (ErrorHandler.Failed(frame.IsOnScreen(out var onScreen)) || onScreen == 0)
                            continue;

                        frame.Show();
                        return;
                    }
                    catch (Exception)
                    {
                        // Stale frame in the snapshot: keep walking.
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("mru cycle failed", ex);
            }
        }

        private static bool TryGetActiveFrame(
            IVsMonitorSelection monitorSelection,
            out IVsWindowFrame frame,
            out FrameRect rect)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            frame = null!;
            rect = default;

            if (ErrorHandler.Failed(monitorSelection.GetCurrentElementValue(
                    (uint)VSConstants.VSSELELEMID.SEID_WindowFrame, out var value)))
                return false;
            if (value is not IVsWindowFrame active) return false;
            if (!TryGetRect(active, out var activeRect)) return false;

            frame = active;
            rect = activeRect;
            return true;
        }

        private static List<(IVsWindowFrame Frame, FrameRect Rect)> EnumerateVisibleFrames(
            IVsUIShell uiShell)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var result = new List<(IVsWindowFrame, FrameRect)>();
            if (ErrorHandler.Failed(uiShell.GetDocumentWindowEnum(out var enumFrames))
                || enumFrames == null)
                return result;

            var batch = new IVsWindowFrame[8];
            while (ErrorHandler.Succeeded(enumFrames.Next((uint)batch.Length, batch, out var fetched))
                   && fetched > 0)
            {
                for (var i = 0; i < fetched; i++)
                {
                    var frame = batch[i];
                    if (frame == null) continue;

                    if (ErrorHandler.Failed(frame.IsOnScreen(out var onScreen)) || onScreen == 0)
                        continue;

                    if (TryGetRect(frame, out var rect))
                        result.Add((frame, rect));
                }
            }
            return result;
        }

        private static bool TryGetRect(IVsWindowFrame frame, out FrameRect rect)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            rect = default;

            // pdwSFP is [out] here: pass an empty slot and it reports the frame
            // mode back. px/py are absolute screen coordinates, cx/cy the size.
            if (ErrorHandler.Failed(frame.GetFramePos(
                    new VSSETFRAMEPOS[1], out _,
                    out var x, out var y, out var cx, out var cy)))
                return false;
            if (cx <= 0 || cy <= 0) return false;

            rect = new FrameRect { Left = x, Top = y, Right = x + cx, Bottom = y + cy };
            return true;
        }

        private static string CaptionOf(IVsWindowFrame frame)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return ErrorHandler.Succeeded(frame.GetProperty((int)__VSFPROPID.VSFPROPID_Caption, out var caption))
                && caption is string text
                ? text
                : "?";
        }

        private static void Remember(IVsWindowFrame frame)
        {
            for (var i = Remembered.Count - 1; i >= 0; i--)
            {
                if (IsSameObject(Remembered[i], frame))
                {
                    Remembered.RemoveAt(i);
                    break;
                }
            }

            Remembered.Add(frame);
            while (Remembered.Count > RememberedCapacity)
                Remembered.RemoveAt(0);
        }

        // Higher means the frame was focused more recently; -1 means never seen.
        private static int RecencyRank(IVsWindowFrame frame)
        {
            for (var i = Remembered.Count - 1; i >= 0; i--)
                if (IsSameObject(Remembered[i], frame))
                    return i;
            return -1;
        }

        // RCWs are not guaranteed to be reference-equal for the same COM object;
        // the underlying IUnknown pointer is the identity that can be trusted.
        private static bool IsSameObject(object a, object b)
        {
            var ptrA = Marshal.GetIUnknownForObject(a);
            var ptrB = Marshal.GetIUnknownForObject(b);
            try
            {
                return ptrA == ptrB;
            }
            finally
            {
                Marshal.Release(ptrA);
                Marshal.Release(ptrB);
            }
        }

        private static bool TryScore(
            string direction,
            FrameRect active,
            FrameRect candidate,
            out int distance,
            out int overlap)
        {
            distance = 0;
            overlap = 0;

            switch (direction)
            {
                case "left":
                    if (candidate.Right > active.Left + EdgeTolerance) return false;
                    distance = active.Left - candidate.Right;
                    overlap = Overlap(active.Top, active.Bottom, candidate.Top, candidate.Bottom);
                    break;
                case "right":
                    if (candidate.Left < active.Right - EdgeTolerance) return false;
                    distance = candidate.Left - active.Right;
                    overlap = Overlap(active.Top, active.Bottom, candidate.Top, candidate.Bottom);
                    break;
                case "up":
                    if (candidate.Bottom > active.Top + EdgeTolerance) return false;
                    distance = active.Top - candidate.Bottom;
                    overlap = Overlap(active.Left, active.Right, candidate.Left, candidate.Right);
                    break;
                case "down":
                    if (candidate.Top < active.Bottom - EdgeTolerance) return false;
                    distance = candidate.Top - active.Bottom;
                    overlap = Overlap(active.Left, active.Right, candidate.Left, candidate.Right);
                    break;
                default:
                    return false;
            }

            // No orthogonal overlap means the candidate is diagonally placed, which
            // is not a neighbor in any Vim sense.
            return overlap > 0;
        }

        private static int Overlap(int aStart, int aEnd, int bStart, int bEnd) =>
            Math.Min(aEnd, bEnd) - Math.Max(aStart, bStart);
    }
}
