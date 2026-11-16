using System;
using System.ComponentModel.Design;
using Microsoft.VisualStudio.Shell;

namespace VSNeo_Extension.Infrastructure;

internal static class VSNeoCommands
{
    private static readonly Guid CommandSet = new Guid("6f1b7c3e-4a52-4d8e-9b1f-3c5e7a9d2b40");

    public static void Register(OleMenuCommandService commands)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var toggle = new OleMenuCommand(
            (_, _) => VSNeo_ExtensionPackage.ToggleEnabled(),
            new CommandID(CommandSet, 0x0108));
        toggle.BeforeQueryStatus += (_, _) =>
            toggle.Text = VSNeoSettings.Enabled ? "VSNeo: Disable" : "VSNeo: Enable";
        commands.AddCommand(toggle);

        var restart = new OleMenuCommand(
            (_, _) => VSNeo_ExtensionPackage.RestartNeovim(),
            new CommandID(CommandSet, 0x0109));
        restart.BeforeQueryStatus += (_, _) =>
            // Enabled in the one state a retry can help. After a live session
            // dies the command stays enabled and explains why it declines:
            // document mirrors still hold the dead session.
            restart.Enabled = !(VSNeo_ExtensionPackage.Session is { IsReady: true });
        commands.AddCommand(restart);

        var diagnostics = new OleMenuCommand(
            (_, _) => VSNeo_ExtensionPackage.ShowDiagnostics(),
            new CommandID(CommandSet, 0x010A));
        commands.AddCommand(diagnostics);
    }
}
