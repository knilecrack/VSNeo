// Polyfill so records and init accessors compile on net472 (the type exists
// only in .NET 5+; the compiler just needs to find it).

#pragma warning disable IDE0130 // Namespace does not match folder structure

namespace System.Runtime.CompilerServices
{
    using System.ComponentModel;

    [EditorBrowsable(EditorBrowsableState.Never)]
    internal static class IsExternalInit
    {
    }
}
