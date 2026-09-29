// Seeky picker embedded in NeoVS — see SeekyPickerController.

namespace VSNeo_Extension.Seeky
{
    using System;
    using VSNeo_Extension.Infrastructure;

    /// <summary>
    /// Keeps the files shared with the standalone SeekyVS extension compiling unchanged:
    /// their SeekyLog.Info/Error calls land here and forward to NeoVS's own log
    /// (%TEMP%\vsneo.log) with a marker, instead of a second log file.
    /// </summary>
    internal static class SeekyLog
    {
        public static void Info(string message) => Log.Write("seeky: " + message);

        public static void Error(string message, Exception ex) => Log.Write("seeky: " + message, ex);
    }
}
