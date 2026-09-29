using System;
using System.ComponentModel.Composition;
using System.Windows.Threading;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.Text.Editor;
using VSNeo_Extension.Nvim;

namespace VSNeo_Extension.Editor
{
    /// <summary>
    /// Answers one question for both interception points: is Visual Studio's own
    /// UI currently owed this keystroke?
    ///
    /// Escape is what makes this load bearing. Whoever claims Escape has to let a
    /// completion list dismiss itself first, or the list becomes impossible to
    /// close. The same applies to j and k while a list is open - but only in
    /// insert/replace, where typing is happening: in normal mode an open tooltip
    /// must not steal motions (the key processor scopes this gate accordingly;
    /// the command filter consults it for Escape regardless of mode).
    ///
    /// Both completion brokers are consulted because Visual Studio has two: modern
    /// Roslyn completion is async, older providers still use the legacy broker, and
    /// which one is live depends on the language and the version. Imports allow
    /// default so that a host missing either one degrades to "not active" instead
    /// of failing composition for the whole assembly.
    /// </summary>
    [Export(typeof(IntelliSenseGate))]
    [PartCreationPolicy(CreationPolicy.Shared)]
    internal sealed class IntelliSenseGate : IPartImportsSatisfiedNotification
    {
        [Import(AllowDefault = true)]
        internal IAsyncCompletionBroker AsyncCompletion { get; set; } = null!;

        [Import(AllowDefault = true)]
        internal ICompletionBroker LegacyCompletion { get; set; } = null!;

        [Import(AllowDefault = true)]
        internal ISignatureHelpBroker SignatureHelp { get; set; } = null!;

        /// <summary>
        /// Completion belongs to insert mode. Outside it every printable key
        /// goes to nvim, so a list that opens there - whatever triggered it -
        /// can only steal the next Escape or arrow and pretend the editor is
        /// taking text. Dismissed as soon as it is triggered, on document views
        /// in normal, visual and operator-pending mode. Subscribing is the
        /// whole cost at composition; the check itself reads the cached mode.
        /// </summary>
        public void OnImportsSatisfied()
        {
            if (AsyncCompletion != null)
                AsyncCompletion.CompletionTriggered += OnCompletionTriggered;
        }

        private static void OnCompletionTriggered(object sender, CompletionTriggeredEventArgs e)
        {
            try
            {
                var view = e.TextView;
                if (view == null || !view.Roles.Contains(PredefinedTextViewRoles.Document)) return;

                var session = VSNeo_ExtensionPackage.Session;
                if (session == null || !session.IsReady) return;

                var mode = session.State.Mode;
                if (mode != VimMode.Normal && mode != VimMode.Visual && mode != VimMode.OperatorPending)
                    return;

                var completion = e.CompletionSession;
                Infrastructure.Log.Key("completion triggered in " + mode + " mode - dismissed");

                // Posted: the broker is still setting the session up while it
                // raises the event, and dismissing from inside that is asking
                // for its state machine to trip over itself.
                var dispatcher = (view as IWpfTextView)?.VisualElement.Dispatcher
                                 ?? Dispatcher.CurrentDispatcher;
#pragma warning disable VSTHRD001
                _ = dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                {
                    try { if (!completion.IsDismissed) completion.Dismiss(); }
                    catch (Exception ex) { Infrastructure.Log.Write("completion dismiss failed", ex); }
                }));
#pragma warning restore VSTHRD001
            }
            catch (Exception ex)
            {
                Infrastructure.Log.Write("completion trigger check failed", ex);
            }
        }

        public bool IsActive(ITextView view)
        {
            if (view == null) return false;

            try
            {
                if (AsyncCompletion != null && AsyncCompletion.IsCompletionActive(view)) return true;
                if (LegacyCompletion != null && LegacyCompletion.IsCompletionActive(view)) return true;
                if (SignatureHelp != null && SignatureHelp.IsSignatureHelpActive(view)) return true;
            }
            catch
            {
                // A broker throwing must not decide the key path. Treating it as
                // inactive keeps VSNeo responsive; the alternative is a dead editor.
            }

            return false;
        }
    }
}
