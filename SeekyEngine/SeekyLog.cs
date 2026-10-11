// The engine's SeekyLog: Seeky's files (external/Seeky) log through this class, so it keeps
// upstream's name and namespace. Writes to the path VSNeo passes with --log
// (%TEMP%\vsneo-seeky-engine.log by default), not upstream's seekyvs.log, so a
// standalone SeekyVS and VSNeo's engine never interleave in one file.

namespace SeekyVS;

using System;
using System.IO;

internal static class SeekyLog
{
    private static readonly object Sync = new();

    private static string logPath = Path.Combine(Path.GetTempPath(), "vsneo-seeky-engine.log");

    /// <summary>Redirects the log. Called once, from Main, before any other line.</summary>
    internal static void SetPath(string path) => logPath = path;

    /// <summary>Logs an informational step.</summary>
    public static void Info(string message) => Write("INFO", message);

    /// <summary>Logs an exception with full stack trace.</summary>
    public static void Error(string message, Exception ex) => Write("ERROR", message + Environment.NewLine + ex);

    private static void Write(string level, string message)
    {
        try
        {
            lock (Sync)
            {
                string? dir = Path.GetDirectoryName(logPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.AppendAllText(
                    logPath,
                    $"{DateTime.Now:HH:mm:ss.fff} [{Environment.ProcessId}] [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Never throw from the logger.
        }
    }
}
