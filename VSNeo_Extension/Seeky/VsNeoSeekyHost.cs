// The extension's side of the VSNeo.Seeky seam: the picker's ISeekyHost, over NeoVS's log,
// its process job, and the nvim session.

namespace VSNeo_Extension.Seeky;

using System;
using System.Diagnostics;
using System.Threading.Tasks;
using VSNeo.Seeky;
using VSNeo_Extension.Infrastructure;
using VSNeo_Extension.Nvim;

/// <summary>Set as <see cref="SeekyHost.Current"/> at package load.</summary>
internal sealed class VsNeoSeekyHost : ISeekyHost
{
    public void Log(string message, Exception? error = null)
    {
        if (error is null)
        {
            Infrastructure.Log.Write(message);
        }
        else
        {
            Infrastructure.Log.Write(message, error);
        }
    }

    public IDisposable? AssignToJob(Process process) => ProcessJob.TryAssign(process);

    public bool NvimReady => VSNeo_ExtensionPackage.Session is { IsReady: true };

    public void EnsureNormalMode()
    {
        NvimSession? session = VSNeo_ExtensionPackage.Session;
        if (session is { IsReady: true }
            && session.State.Mode is VimMode.Insert or VimMode.Replace or VimMode.Visual)
        {
            session.Input("<Esc>");
        }
    }

    public void Input(string keys)
    {
        NvimSession? session = VSNeo_ExtensionPackage.Session;
        if (session is { IsReady: true })
        {
            session.Input(keys);
        }
    }

    public Task<object?> RequestAsync(string method, params object[] args) =>
        VSNeo_ExtensionPackage.Session is { IsReady: true } session
            ? session.RequestAsync(method, args)
            : Task.FromResult<object?>(null);
}
