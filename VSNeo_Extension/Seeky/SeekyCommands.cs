// Seeky picker embedded in NeoVS — the Visual Studio commands behind the Ctrl+Shift+Alt chords.

namespace VSNeo_Extension.Seeky;

using System;
using System.ComponentModel.Design;
using Microsoft.VisualStudio.Shell;
using VSNeo_Extension.Nvim;

/// <summary>
/// Wires the Seeky commands declared in <c>VSNeo_Extension.vsct</c> to the picker. The chords
/// live on the Visual Studio side because they cannot live in nvim: KeyEncoder never sends a
/// Ctrl+Alt chord there. Everything else is shared with the <c>vsneo.seeky</c> path.
/// </summary>
internal static class SeekyCommands
{
    /// <summary>The command set in <c>VSNeo_Extension.vsct</c> (guidVSNeoCmdSet).</summary>
    private static readonly Guid CommandSet = new Guid("6f1b7c3e-4a52-4d8e-9b1f-3c5e7a9d2b40");

    /// <summary>Registers every Seeky command. UI thread, once per package load.</summary>
    internal static void Register(OleMenuCommandService commands)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        Add(commands, 0x0100, "files");
        Add(commands, 0x0101, "grep");
        Add(commands, 0x0102, "grepword");
        Add(commands, 0x0103, "lines");
        Add(commands, 0x0104, "symbols");
        Add(commands, 0x0105, "outline");
        Add(commands, 0x0106, "git");
        Add(commands, 0x0107, "resume");
    }

    private static void Add(OleMenuCommandService commands, int id, string mode) =>
        commands.AddCommand(new MenuCommand((_, _) => Execute(mode), new CommandID(CommandSet, id)));

    /// <summary>
    /// Runs on the UI thread, as every Visual Studio command does. Never throws into the shell:
    /// a picker that fails to open logs, it does not put up an error dialog.
    /// </summary>
    private static void Execute(string mode)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            RecordJump();
            if (mode == "grepword")
            {
                SeekyPickerController.Show("grep", SeekyPickerController.EditorSearchTerm() ?? string.Empty);
            }
            else
            {
                SeekyPickerController.Show(mode, string.Empty);
            }
        }
        catch (Exception ex)
        {
            SeekyLog.Error($"Seeky command '{mode}' failed", ex);
        }
    }

    /// <summary>
    /// What <c>vsneo.seeky</c> does in Lua: a pick lands far away, so the jump is recorded
    /// first and <c>''</c> comes back. Normal mode only — the chords work in insert too, and
    /// <c>:normal!</c> over RPC flaps nvim's mode out of insert and back (the reason
    /// <c>folds_set</c> defers too). Fire-and-forget; a lost mark costs one <c>''</c>.
    /// </summary>
    private static void RecordJump()
    {
        NvimSession? session = VSNeo_ExtensionPackage.Session;
        if (session is null || !session.IsReady || session.State.Mode != VimMode.Normal)
        {
            return;
        }

        _ = session.RequestAsync("nvim_command", "normal! m'");
    }
}
