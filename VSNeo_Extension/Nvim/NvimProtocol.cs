namespace VSNeo_Extension.Nvim;

/// <summary>
/// The rpcnotify/rpcrequest contract between this assembly and Lua/vsneo.lua.
///
/// The companion returns <see cref="Version"/> from its chunk; a mismatch means
/// the two halves came from different builds - a stale vsneo.lua beside the
/// DLL - and the session is not safe to use. Bump both sides together whenever
/// the contract changes.
/// </summary>
internal static class NvimProtocol
{
    public const int Version = 2;
}
