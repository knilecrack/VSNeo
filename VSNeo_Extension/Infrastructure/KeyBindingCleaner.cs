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
        private static readonly string[] Chords =
        {
            "Ctrl+E",   // scroll down one line; in VS a chord prefix, so unbinding is the only option
            "Ctrl+D",   // scroll half a page down; VS Edit.Duplicate
            "Ctrl+U",   // scroll half a page up; VS Edit.MakeLowercase
            "Ctrl+B",   // scroll a page back; VS bookmark and toolbox bindings
            "Ctrl+Y",   // scroll up one line; VS Redo - u and Ctrl+R remain the Vim way
            "Ctrl+R",   // redo; in VS the prefix of the whole Refactor chord family
            "Ctrl+W",   // window command prefix; VS Edit.SelectCurrentWord
        };

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

        public static void Run(DTE dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (dte == null) return;

            var clock = Stopwatch.StartNew();
            int removed = 0, inspected = 0;

            try
            {
                foreach (Command command in dte.Commands)
                {
                    if (command == null) continue;
                    inspected++;

                    if (!(command.Bindings is object[] bindings) || bindings.Length == 0) continue;

                    var keep = bindings.Where(b => !ShouldRemove((string)b)).ToArray();
                    if (keep.Length == bindings.Length) continue;

                    var dropped = bindings.Except(keep).Select(b => b as string);
                    try
                    {
                        command.Bindings = keep;
                        removed += bindings.Length - keep.Length;
                        Log.Write("unbound " + string.Join(", ", dropped)
                                  + " from " + SafeName(command));
                    }
                    catch (Exception ex)
                    {
                        // Some commands refuse rebinding. Not worth failing over.
                        Log.Write("could not unbind " + SafeName(command), ex);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("key binding cleanup failed", ex);
            }

            Log.Write("key bindings: inspected " + inspected + ", removed " + removed
                      + ", took " + clock.ElapsedMilliseconds + "ms");

            ReportSurvivors(dte);
        }

        /// <summary>
        /// Names anything still holding one of our chords after the pass.
        ///
        /// A single surviving binding in any scope keeps the shell waiting for the
        /// second key of a chord, so the keystroke never reaches anyone - which is
        /// exactly how Ctrl+E stayed dead through twelve successful removals. Some
        /// commands also refuse to be rebound, and silence there is indistinguishable
        /// from success.
        /// </summary>
        private static void ReportSurvivors(DTE dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var survivors = new List<string>();
            try
            {
                foreach (Command command in dte.Commands)
                {
                    if (command == null) continue;
                    if (!(command.Bindings is object[] bindings)) continue;

                    foreach (var binding in bindings)
                        if (ShouldRemove(binding as string))
                            survivors.Add(SafeName(command) + "  <-  " + binding);
                }
            }
            catch (Exception ex)
            {
                Log.Write("could not verify key bindings", ex);
                return;
            }

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
        private static bool ShouldRemove(string binding)
        {
            if (string.IsNullOrEmpty(binding)) return false;

            int split = binding.IndexOf("::", StringComparison.Ordinal);
            if (split < 0) return false;

            var scope = binding.Substring(0, split);
            var keys = binding.Substring(split + 2);

            if (!ScopeMatters(scope)) return false;

            return Chords.Any(c =>
                keys.StartsWith(c + ",", StringComparison.OrdinalIgnoreCase) ||
                keys.Equals(c, StringComparison.OrdinalIgnoreCase));
        }

        private static string SafeName(Command command)
        {
            try { return string.IsNullOrEmpty(command.Name) ? "(unnamed)" : command.Name; }
            catch { return "(unreadable)"; }
        }
    }
}
