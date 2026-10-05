// SeekyVS — Visual Studio 2026 port spike for the Seeky VS Code extension.

namespace SeekyVS;

/// <summary>
/// A half-open [<see cref="Start"/>, <see cref="End"/>) span of UTF-16 char indices, used for the
/// highlight spans the page draws: into a grep match's line
/// (<see cref="FffNativeClient.GrepMatch.Ranges"/>) or into a symbol name
/// (<see cref="SymbolIndex.Hit.NameRanges"/>).
/// </summary>
/// <remarks>
/// Its own file rather than a nested type: both producers need it, and having the P/Invoke layer
/// reach into the symbol picker for a two-int struct got the dependency backwards.
/// </remarks>
internal readonly record struct SeekyRange(int Start, int End);
