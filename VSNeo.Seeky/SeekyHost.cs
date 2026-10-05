// Seeky picker embedded in NeoVS — the seam between the picker and the extension hosting it.

namespace VSNeo.Seeky;

using System;
using System.Diagnostics;
using System.Threading.Tasks;

/// <summary>
/// What the picker needs from the extension that hosts it. The picker never sees nvim or the
/// package itself: VSNeo_Extension implements this and sets <see cref="SeekyHost.Current"/>
/// at package load, before any picker can open.
/// </summary>
internal interface ISeekyHost
{
    /// <summary>Into the host's log; <paramref name="error"/> null for an info line.</summary>
    void Log(string message, Exception? error = null);

    /// <summary>Ties <paramref name="process"/>'s lifetime to devenv's (kill on close).
    /// Disposing the result releases it. Null when the job could not be made.</summary>
    IDisposable? AssignToJob(Process process);

    /// <summary>Whether nvim is up; every call below is a no-op (or null) when it is not.</summary>
    bool NvimReady { get; }

    /// <summary>Leaves insert, replace or visual - a pick acts from normal mode.</summary>
    void EnsureNormalMode();

    /// <summary>Keys typed into nvim (nvim_input notation).</summary>
    void Input(string keys);

    /// <summary>One nvim API request (nvim_call_function, nvim_exec_lua, ...).</summary>
    Task<object?> RequestAsync(string method, params object[] args);
}

/// <summary>Where the picker finds its host. Null outside Visual Studio (unit tests).</summary>
internal static class SeekyHost
{
    internal static ISeekyHost? Current { get; set; }
}
