using System;
using System.Collections.Generic;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using VSNeo_Extension.Infrastructure;
using VSNeo_Extension.Nvim;

namespace VSNeo_Extension.Editor
{
    /// <summary>
    /// Labeled jump to any open document tab (PeasyMotion's document-tab mode,
    /// rebuilt on the overlay conversation).
    ///
    /// Begin rewrites every visible tab's caption to "A|Name" through
    /// VsFramePropID.OverrideCaption - the shell repaints the tab well, no WPF
    /// adornment could reach it otherwise - and hands key reading to nvim, where
    /// the overlay interaction delivers Escape back as a cancel. Pick Show()s
    /// the chosen frame. Either way the captions are then restored.
    ///
    /// UI thread only; failures clear the labels and otherwise do nothing.
    /// </summary>
    internal static class TabJumper
    {
        // Home row first: the most reachable letters label the first tabs.
        private const string LabelChars = "asdfghjklqwertyuiopzxcvbnm";

        // The interaction in flight. Empty outside of a Begin/Pick pair.
        private static readonly List<(string Label, IVsWindowFrame Frame)> Pending =
            new List<(string, IVsWindowFrame)>();

        public static void Begin(NvimSession session, IVsUIShell? uiShell)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                // A stale interaction (VS lost focus mid-jump, say) gets its
                // captions back before a new round starts.
                Clear();

                if (uiShell == null) return;

                var frames = EnumerateDocumentTabs(uiShell);
                var count = Math.Min(frames.Count, LabelChars.Length);
                for (var i = 0; i < count; i++)
                {
                    var label = LabelChars[i].ToString();
                    Pending.Add((label, frames[i]));
                    SetLabel(frames[i], label);
                }

                if (Pending.Count == 0) return;

                // nvim reads the pick: letters reach it like any normal-mode
                // typing, and the overlay interaction is what routes Escape to
                // it as a cancel.
                _ = session.RequestAsync(
                    "nvim_exec_lua", "vsneo._tab_jump_read()", Array.Empty<object>());
            }
            catch (Exception ex)
            {
                Log.Write("tab jump begin failed", ex);
                Clear();
            }
        }

        public static void Pick(string label)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                IVsWindowFrame? target = null;
                foreach (var (pendingLabel, frame) in Pending)
                {
                    if (pendingLabel == label)
                    {
                        target = frame;
                        break;
                    }
                }

                target?.Show();
            }
            catch (Exception ex)
            {
                Log.Write("tab jump pick failed", ex);
            }
            finally
            {
                Clear();
            }
        }

        private static void Clear()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            foreach (var (_, frame) in Pending)
            {
                try
                {
                    frame.SetProperty((int)VsFramePropID.OverrideCaption, null);
                    RefreshCaption(frame);
                }
                catch (Exception)
                {
                    // A tab closed mid-interaction; nothing to restore.
                }
            }
            Pending.Clear();
        }

        private static void SetLabel(IVsWindowFrame frame, string label)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // The three-character prefix replaces the caption's first three
            // characters rather than prepending, so tab widths do not reflow
            // while the labels are up.
            var caption = ErrorHandler.Succeeded(
                frame.GetProperty((int)__VSFPROPID.VSFPROPID_Caption, out var value))
                && value is string text
                ? text
                : string.Empty;

            var prefix = label.ToUpperInvariant() + " |";
            frame.SetProperty(
                (int)VsFramePropID.OverrideCaption,
                prefix + (caption.Length > prefix.Length ? caption.Substring(prefix.Length) : string.Empty));
            RefreshCaption(frame);
        }

        // OverrideCaption alone does not repaint; the caption refresh has to be
        // nudged through the frame's text view.
        private static void RefreshCaption(IVsWindowFrame frame)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            VsShellUtilities.GetTextView(frame)?.UpdateViewFrameCaption();
        }

        private static List<IVsWindowFrame> EnumerateDocumentTabs(IVsUIShell uiShell)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var result = new List<IVsWindowFrame>();
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

                    // No IsOnScreen filter, deliberately: it reports only each
                    // group's selected tab, which would label one tab per group.
                    // Jumping straight to a background tab is the point of the
                    // feature; PeasyMotion's tab mode skips the check for the
                    // same reason.
                    result.Add(frame);
                }
            }
            return result;
        }
    }
}
