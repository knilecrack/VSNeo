using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using VSNeo_Extension.Infrastructure;
using VSNeo_Extension.Nvim;

namespace VSNeo_Extension.Editor;

/// <summary>
/// Answers nvim's vsneo_info request (the :VSNeoInfo user command) and builds
/// the same report for the Tools &gt; VSNeo: Diagnostics command, which must
/// work when nvim never started.
///
/// Everything here is cached state: no services, no UI thread, no round trips.
/// rpcrequest blocks nvim until the answer arrives.
/// </summary>
internal static class InfoRequest
{
    public const string Method = "vsneo_info";

    private static readonly string ExtensionVersion = ReadExtensionVersion();

    public static Task<object?> HandleAsync(NvimSession session) =>
        Task.FromResult<object?>(Build(session));

    public static string Build(NvimSession? session)
    {
        var text = new StringBuilder();
        text.AppendLine("VSNeo diagnostics");
        text.AppendLine("  extension:      " + ExtensionVersion);
        text.AppendLine("  visual studio:  " + (VSNeo_ExtensionPackage.VisualStudioVersion ?? "unknown"));
        text.AppendLine("  session:        " + DescribeSession(session));
        text.AppendLine("  neovim:         " + (session?.NvimPath ?? VSNeo_ExtensionPackage.ResolveNvimPath())
                         + " (" + (session?.NvimVersion ?? "not connected") + ")");
        text.AppendLine("  protocol:       " + NvimProtocol.Version);
        text.AppendLine("  mode:           " + (session == null ? "n/a" : session.State.Mode.ToString()));
        text.AppendLine("  buffer:         " + (session?.State.CurrentBufferPath ?? "n/a"));
        text.AppendLine("  enabled:        " + VSNeoSettings.Enabled);
        text.AppendLine("  nvim path opt:  "
                         + (string.IsNullOrWhiteSpace(VSNeoSettings.NeovimPath) ? "(unset)" : VSNeoSettings.NeovimPath));
        text.AppendLine("  log:            " + Log.Path);

        string? failure = session?.StartupFailure ?? session?.LastFault?.Message;
        text.AppendLine("  last failure:   " + (string.IsNullOrEmpty(failure) ? "(none)" : failure));

        string? stderr = session?.StdErrTail;
        if (!string.IsNullOrEmpty(stderr))
        {
            text.AppendLine("  nvim stderr:");
            text.AppendLine(stderr);
        }

        return text.ToString().TrimEnd();
    }

    private static string DescribeSession(NvimSession? session)
    {
        if (session == null)
        {
            return "no session";
        }

        return session.IsReady ? "ready" : "fallback (Visual Studio input)";
    }

    /// <summary>
    /// The version users see on the Marketplace listing, from the manifest
    /// installed beside the DLL. The assembly version is always 1.0.0.0 (CI
    /// stamps only the manifest), so it is the fallback, not the answer.
    /// </summary>
    private static string ReadExtensionVersion()
    {
        try
        {
            string? directory = Path.GetDirectoryName(typeof(InfoRequest).Assembly.Location);
            string manifest = Path.Combine(directory ?? ".", "extension.vsixmanifest");
            if (File.Exists(manifest))
            {
                XElement? metadata = XDocument.Load(manifest).Root?
                    .Elements().FirstOrDefault(e => e.Name.LocalName == "Metadata");
                XElement? identity = metadata?
                    .Elements().FirstOrDefault(e => e.Name.LocalName == "Identity");
                string? version = identity?.Attribute("Version")?.Value;
                if (version != null && version.Length > 0)
                {
                    return version;
                }
            }
        }
        catch
        {
            // Diagnostics must not fail on a missing or malformed manifest.
        }

        return typeof(InfoRequest).Assembly.GetName().Version?.ToString() ?? "unknown";
    }
}
