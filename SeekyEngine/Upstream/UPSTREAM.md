# Upstream

Copied verbatim from https://github.com/knilecrack/Seeky, `vs2026/SeekyVS/`,
at commit `33a00de` (2026-09-29, "fix(library): udpated library to latest one
0.11"), together with `Tools/fff_c.dll` (fff v0.11.0,
`c-lib-x86_64-pc-windows-msvc.dll`, sha256 in `../Tools/fff_c.dll.sha256`).

    FffNativeClient.cs  SymbolIndex.cs  SymbolClassifier.cs  FuzzyMatcher.cs  SeekyRange.cs

Do not edit these files here. Change them in Seeky and copy them over again;
anything the engine needs differently goes in the files beside this folder
(`SeekyLog.cs` is the engine's own, under upstream's name).

`tools/sync-seeky.ps1 -SeekyPath <Seeky clone>` checks these against the pinned
commit (CI runs it), lists upstream commits since the pin that touched the files
VSNeo ports by hand (`VSNeo_Extension/Seeky/`), and with `-Update [-Ref <commit>]`
copies these over and moves the pin.
