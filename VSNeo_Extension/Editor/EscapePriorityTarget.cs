using System;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;

namespace VSNeo_Extension.Editor
{
    /// <summary>
    /// Sees Escape before any command filter on the view does.
    ///
    /// VsNeoCommandFilter joins the view's IOleCommandTarget chain when the
    /// view is created, and every filter added after it sits ahead of it. A
    /// language service that attaches its own filter later (the C++ service
    /// does its own command handling) can then take CANCEL to close its
    /// completion list and stop the routing there: the list closes, nvim is
    /// never told, and insert mode survives the Escape - the next G or / is
    /// typed into the file. A priority command target is consulted before the
    /// whole chain, so the order of filters no longer decides it.
    ///
    /// Only the insert-mode Escape is claimed here: that is the one that
    /// changes modes, and the one whose loss hurts. It is handled through the
    /// focused view's own filter (same caret sync, same swallow rule), which
    /// then lets the same keystroke pass when it reaches the view. Everything
    /// else returns not-supported, which lets the shell route on untouched.
    /// </summary>
    internal sealed class EscapePriorityTarget : IOleCommandTarget
    {
        public int Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (pguidCmdGroup == VSConstants.VSStd2K)
            {
                try
                {
                    var filter = VsNeoCommandFilter.Focused;
                    if (filter != null)
                    {
                        if (nCmdID == (uint)VSConstants.VSStd2KCmdID.CANCEL)
                        {
                            if (filter.TryClaimInsertEscape(out bool swallow) && swallow)
                                return VSConstants.S_OK;
                        }
                        // The navigation keys in normal, visual and operator-pending
                        // mode, for the same reason as Escape: a filter ahead of
                        // ours in the chain can eat them. See TryClaimNavigationKey.
                        else if (filter.TryClaimNavigationKey(pguidCmdGroup, nCmdID))
                        {
                            return VSConstants.S_OK;
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Nothing above a priority target catches: a throw here
                    // would break the key everywhere in Visual Studio.
                    Infrastructure.Log.Write("priority key handling failed", ex);
                }
            }

            return (int)Constants.OLECMDERR_E_NOTSUPPORTED;
        }

        public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText) =>
            (int)Constants.OLECMDERR_E_NOTSUPPORTED;
    }
}
