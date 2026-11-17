// Polyfill so records and init accessors compile on net472 (the type exists
// only in .NET 5+; the compiler just needs to find it).


using System.ComponentModel;

#pragma warning disable IDE0130 // Namespace does not match folder structure

namespace System.Runtime.CompilerServices;

[EditorBrowsable(EditorBrowsableState.Never)]
internal static class IsExternalInit
{
}
