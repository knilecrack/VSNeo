// seeky-engine: Seeky's fff search engine as a child process of Visual Studio.
//
//   seeky-engine --pipe <name> [--parent <pid>] [--log <path>]
//
// Serves one client on the named pipe and exits when it disconnects, or when the
// parent process ends (VSNeo also puts this process in a kill-on-close job; the
// parent watch covers a client that never connected).

namespace SeekyVS;

using System;
using System.Diagnostics;
using System.Threading.Tasks;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        string? pipe = null;
        int parent = 0;
        for (int i = 0; i + 1 < args.Length; i += 2)
        {
            switch (args[i])
            {
                case "--pipe": pipe = args[i + 1]; break;
                case "--parent": int.TryParse(args[i + 1], out parent); break;
                case "--log": SeekyLog.SetPath(args[i + 1]); break;
            }
        }

        if (string.IsNullOrEmpty(pipe))
        {
            Console.Error.WriteLine("usage: seeky-engine --pipe <name> [--parent <pid>] [--log <path>]");
            return 2;
        }

        SeekyLog.Info($"engine starting: pipe '{pipe}', parent {parent}");
        if (parent > 0)
        {
            WatchParent(parent);
        }

        try
        {
            using var server = new EngineServer();
            await server.RunAsync(pipe!);
            SeekyLog.Info("engine: client disconnected, exiting");
            return 0;
        }
        catch (Exception ex)
        {
            SeekyLog.Error("engine failed", ex);
            return 1;
        }
    }

    private static void WatchParent(int pid)
    {
        Process parent;
        try
        {
            parent = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            SeekyLog.Info($"engine: parent {pid} already gone, exiting");
            Environment.Exit(0);
            return;
        }

        _ = Task.Run(() =>
        {
            parent.WaitForExit();
            SeekyLog.Info($"engine: parent {pid} exited, exiting");
            Environment.Exit(0);
        });
    }
}
