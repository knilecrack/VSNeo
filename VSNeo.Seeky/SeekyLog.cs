// Seeky picker embedded in NeoVS — see SeekyPickerController.

namespace VSNeo.Seeky
{
    using System;

    /// <summary>
    /// Keeps the files shared with the standalone SeekyVS extension compiling unchanged:
    /// their SeekyLog.Info/Error calls land here and forward to the host's log (NeoVS's
    /// %TEMP%\vsneo.log) with a marker, instead of a second log file. Silent with no host.
    /// </summary>
    internal static class SeekyLog
    {
        public static void Info(string message) => SeekyHost.Current?.Log("seeky: " + message);

        public static void Error(string message, Exception ex) => SeekyHost.Current?.Log("seeky: " + message, ex);
    }
}
