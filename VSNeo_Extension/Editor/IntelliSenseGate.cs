using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Text.Editor;

namespace VSNeo_Extension.Editor
{
    /// <summary>
    /// Answers one question for both interception points: is Visual Studio's own
    /// UI currently owed this keystroke?
    ///
    /// Escape is what makes this load bearing. Whoever claims Escape has to let a
    /// completion list dismiss itself first, or the list becomes impossible to
    /// close. The same applies to j and k while a list is open.
    ///
    /// Both completion brokers are consulted because Visual Studio has two: modern
    /// Roslyn completion is async, older providers still use the legacy broker, and
    /// which one is live depends on the language and the version. Imports allow
    /// default so that a host missing either one degrades to "not active" instead
    /// of failing composition for the whole assembly.
    /// </summary>
    [Export(typeof(IntelliSenseGate))]
    [PartCreationPolicy(CreationPolicy.Shared)]
    internal sealed class IntelliSenseGate
    {
        [Import(AllowDefault = true)]
        internal IAsyncCompletionBroker AsyncCompletion { get; set; } = null!;

        [Import(AllowDefault = true)]
        internal ICompletionBroker LegacyCompletion { get; set; } = null!;

        [Import(AllowDefault = true)]
        internal ISignatureHelpBroker SignatureHelp { get; set; } = null!;

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
