using System.Diagnostics.CodeAnalysis;

// VSSDK.BuildTools 18.12 bans SemaphoreSlim (BannedApiAnalyzers) to steer UI-thread code to JTF.
// NvimRpcClient's write lock is only ever taken with WaitAsync on the thread pool, never on the UI thread.
[assembly: SuppressMessage("ApiDesign", "RS0030", Scope = "type", Target = "~T:VSNeo_Extension.Nvim.NvimRpcClient",
    Justification = "The write lock is awaited off the UI thread only; no JTF deadlock is possible.")]
