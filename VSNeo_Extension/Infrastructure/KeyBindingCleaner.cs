using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using EnvDTE;
using Microsoft.VisualStudio.Shell;

namespace VSNeo_Extension.Infrastructure
{
    /// <summary>
    /// Removes Visual Studio key bindings that Vim needs for itself.
    ///
    /// Most conflicting chords can be dealt with far less invasively: Ctrl+D and
    /// Ctrl+U become ordinary commands, and a command can be claimed in
    /// VsNeoCommandFilter without touching anyone's configuration. Chord *prefixes*
    /// cannot. Ctrl+E in Visual Studio is the first key of Ctrl+E,Ctrl+D and
    /// Ctrl+E,Ctrl+C, so pressing it fires no command at all - the shell just waits
    /// for a second key, silently, and there is nothing to intercept. Unbinding is
    /// the only way to get the key back, and it is what VsVim does for the same
    /// reason.
    ///
    /// Deliberately narrow. Every removal is logged with the command it came from,
    /// so it can be put back by hand, and Tools > Options > Keyboard > Reset undoes
    /// the lot.
    /// </summary>
    internal static class KeyBindingCleaner
    {
        /// <summary>
        /// Chords to take from Visual Studio, matched against the key part of a
        /// binding. Kept to what genuinely cannot be handled by a command filter.
        ///
        /// Ctrl+F is deliberately absent even though Vim wants it for page-forward:
        /// it is Find, it does map to a command, and taking it would surprise
        /// anybody who ever types Ctrl+F out of habit.
        /// </summary>
        private static readonly string[] _chords =
        {
            "Ctrl+E",   // scroll down one line; in VS a chord prefix, so unbinding is the only option
            "Ctrl+D",   // scroll half a page down; VS Edit.Duplicate
            "Ctrl+U",   // scroll half a page up; VS Edit.MakeLowercase
            "Ctrl+B",   // scroll a page back; VS bookmark and toolbox bindings
            "Ctrl+Y",   // scroll up one line; VS Redo - u and Ctrl+R remain the Vim way
            "Ctrl+R",   // redo; in VS the prefix of the whole Refactor chord family
            "Ctrl+W",   // window command prefix; VS Edit.SelectCurrentWord
            "Ctrl+O",   // jumplist back; VS File.OpenFile
            "Ctrl+I",   // jumplist forward; VS Edit.IncrementalSearch
        };

        /// <summary>
        /// The Seeky picker's commands (VSNeo_Extension.vsct). Whatever chords they
        /// are bound to *now* - the vsct defaults or the user's own rebinding - are
        /// taken from every other command: Visual Studio resolves two bindings on
        /// one chord in favor of the other command, and Live Share ships one on
        /// Ctrl+Shift+Alt+O (pressing it started Live Share, a 6 s UI stall,
        /// instead of the picker). Read fresh on every run, never a fixed list: a
        /// chord the user moved Seeky off is theirs to give to anything else, and
        /// a fixed list took it back at every startup.
        /// </summary>
        private static readonly string[] _seekyCommands =
        {
            "Tools.SeekyFindFiles", "Tools.SeekyLiveGrep", "Tools.SeekyGrepWord",
            "Tools.SeekyCurrentFile", "Tools.SeekySymbols", "Tools.SeekyDocumentOutline",
            "Tools.SeekyGitModified", "Tools.SeekyResume",
        };

        private static bool IsSeekyCommand(string name) =>
            _seekyCommands.Contains(name, StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The key parts of the Seeky commands' current bindings, normalized (see
        /// <see cref="NormalizeKeys"/>). Eight direct lookups rather than a second
        /// walk over every command. A command that cannot be read contributes
        /// nothing - then nothing is taken on its behalf.
        /// </summary>
        private static HashSet<string> CurrentSeekyChords(DTE dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var chords = new HashSet<string>(StringComparer.Ordinal);
            foreach (var name in _seekyCommands)
            {
                try
                {
                    if (dte.Commands.Item(name) is Command command
                        && command.Bindings is object[] bindings)
                    {
                        foreach (var b in bindings)
                        {
                            var keys = KeysOf(b as string);
                            if (keys != null) chords.Add(NormalizeKeys(keys));
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Write("key bindings: could not read " + name, ex);
                }
            }

            return chords;
        }

        /// <summary>
        /// Every scope, not a chosen few.
        ///
        /// Restricting this to Text Editor and Global was not enough: Ctrl+E stayed
        /// dead after those were cleared, because a chord bound in *any* scope still
        /// puts the shell into "waiting for the second key" and swallows the
        /// keystroke. A chord prefix has to be unbound everywhere or it is not
        /// unbound at all.
        /// </summary>
        private static bool ScopeMatters(string scope) => true;

        /// <summary>
        /// UI thread (DTE is single-threaded COM), but not in one go: the walk is
        /// several thousand COM calls, and done synchronously it held the UI
        /// thread for the duration at every startup. Every batch of commands it
        /// yields to the message pump - JoinableTaskFactory's context brings it
        /// back onto the main thread - so the shell stays responsive while the
        /// chords are still being freed.
        /// </summary>
        public static async System.Threading.Tasks.Task RunAsync(DTE dte)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (dte == null) return;

            var clock = Stopwatch.StartNew();
            int removed = 0, inspected = 0;
            var survivors = new List<string>();
            var seekyChords = CurrentSeekyChords(dte);
            Log.Write("key bindings: Seeky chords " + (seekyChords.Count == 0 ? "(none)" : string.Join(" | ", seekyChords)));

            try
            {
                foreach (Command command in dte.Commands)
                {
                    if (command == null) continue;
                    inspected++;

                    if (inspected % 250 == 0)
                    {
                        await System.Threading.Tasks.Task.Yield();
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    }

                    if (!(command.Bindings is object[] bindings) || bindings.Length == 0) continue;

                    bool seeky = IsSeekyCommand(SafeName(command));
                    // 'as', not a cast: one non-string element used to throw
                    // InvalidCastException and the outer catch abandoned the
                    // whole pass, leaving every chord after it bound.
                    var keep = bindings.Where(b => !ShouldRemove(b as string, seeky, seekyChords)).ToArray();
                    if (keep.Length == bindings.Length) continue;

                    var dropped = bindings.Except(keep).Select(b => b as string);
                    try
                    {
                        command.Bindings = keep;
                        removed += bindings.Length - keep.Length;
                        Log.Write("unbound " + string.Join(", ", dropped)
                                  + " from " + SafeName(command));

                        // Some commands accept the assignment and keep the binding
                        // anyway, and silence there is indistinguishable from
                        // success. Re-reading is a COM call, so only the commands
                        // just touched are verified - a single surviving binding
                        // in any scope keeps the shell waiting for the second key
                        // of a chord, and the keystroke never reaches anyone.
                        if (command.Bindings is object[] after)
                            foreach (var b in after)
                                if (ShouldRemove(b as string, seeky, seekyChords))
                                    survivors.Add(SafeName(command) + "  <-  " + b);
                    }
                    catch (Exception ex)
                    {
                        // Some commands refuse rebinding. Not worth failing over,
                        // but the binding is still live, so it is a survivor.
                        Log.Write("could not unbind " + SafeName(command), ex);
                        foreach (var d in dropped)
                            survivors.Add(SafeName(command) + "  <-  " + d);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("key binding cleanup failed", ex);
            }

            Log.Write("key bindings: inspected " + inspected + ", removed " + removed
                      + ", took " + clock.ElapsedMilliseconds + "ms");

            if (survivors.Count == 0)
            {
                Log.Write("key bindings: none of the claimed chords remain bound");
                return;
            }

            Log.Write("key bindings: " + survivors.Count + " STILL BOUND, so those chords stay dead:");
            foreach (var s in survivors.Take(20)) Log.Write("    " + s);
        }

        /// <summary>
        /// A binding looks like "Text Editor::Ctrl+E, Ctrl+D". Matching the key part
        /// on a prefix is what catches the two-key chords as well as the bare one.
        /// </summary>
        private static bool ShouldRemove(string? binding, bool fromSeekyCommand, HashSet<string> seekyChords)
        {
            if (string.IsNullOrEmpty(binding)) return false;

            // net472's reference assemblies carry no [NotNullWhen] on
            // IsNullOrEmpty, so the guard above does not narrow for the compiler.
            int split = binding!.IndexOf("::", StringComparison.Ordinal);
            if (split < 0) return false;

            var scope = binding.Substring(0, split);
            var keys = binding.Substring(split + 2);

            if (!ScopeMatters(scope)) return false;

            return _chords.Any(c =>
                keys.StartsWith(c + ",", StringComparison.OrdinalIgnoreCase) ||
                keys.Equals(c, StringComparison.OrdinalIgnoreCase))
                || (!fromSeekyCommand && seekyChords.Contains(NormalizeKeys(keys)));
        }

        /// <summary>The key part of "Scope::Keys", or null.</summary>
        private static string? KeysOf(string? binding)
        {
            if (string.IsNullOrEmpty(binding)) return null;
            int split = binding!.IndexOf("::", StringComparison.Ordinal);
            return split < 0 ? null : binding.Substring(split + 2);
        }

        /// <summary>
        /// A key sequence with each chord's modifiers sorted, so "Ctrl+Alt+Shift+O"
        /// and "Ctrl+Shift+Alt+O" compare equal. Two-key sequences keep their
        /// order ("Ctrl+K, Ctrl+S").
        /// </summary>
        private static string NormalizeKeys(string keys) =>
            string.Join(", ", keys.Split(new[] { ", " }, StringSplitOptions.None).Select(NormalizeChord));

        /// <summary>
        /// One chord as its sorted modifiers plus the key. The key is whatever
        /// follows the last '+' (which keeps "Ctrl+Shift+Alt+," intact); a
        /// one-character key ("+") has no modifiers.
        /// </summary>
        private static string NormalizeChord(string chord)
        {
            string k = chord.Trim();
            if (k.Length < 2) return k.ToUpperInvariant();

            int last = k.LastIndexOf('+', k.Length - 2);
            if (last < 0) return k.ToUpperInvariant();

            var mods = k.Substring(0, last).Split('+')
                .Select(m => m.Trim().ToUpperInvariant())
                .OrderBy(m => m, StringComparer.Ordinal);
            return string.Join("+", mods) + "+" + k.Substring(last + 1).Trim().ToUpperInvariant();
        }

        private static string SafeName(Command command)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try { return string.IsNullOrEmpty(command.Name) ? "(unnamed)" : command.Name; }
            catch { return "(unreadable)"; }
        }
    }
}
