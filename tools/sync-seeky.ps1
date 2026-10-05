<#
.SYNOPSIS
    Shows what Seeky (the external/Seeky submodule) gained, and moves VSNeo onto it.

.DESCRIPTION
    seeky-engine.exe compiles Seeky's fff client, symbol index, classifier and matcher,
    and ships its fff_c.dll, straight from the submodule - taking new Seeky work for
    those is just moving the submodule. VSNeo.Seeky's files are net472 ports with VSNeo's
    additions, so they cannot come from the submodule yet; for them this lists the
    Seeky commits between the pinned commit and -Ref that touched each one, i.e. what
    still has to be brought across by hand.

    Without -Update: the report only. With -Update: the submodule moves to -Ref too;
    then build, and commit with  git add external/Seeky.

.EXAMPLE
    ./tools/sync-seeky.ps1
    ./tools/sync-seeky.ps1 -Update
    ./tools/sync-seeky.ps1 -Ref 1a2b3c4 -Update
#>
param(
    # Where to compare to (and move to with -Update). Default: Seeky's default branch.
    [string]$Ref = 'origin/HEAD',

    [switch]$Update
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$sub = Join-Path $repo 'external/Seeky'
if (-not (Test-Path (Join-Path $sub 'vs2026'))) {
    throw "external/Seeky is not checked out. Run: git submodule update --init"
}

# VSNeo.Seeky file -> the Seeky file it ports (under vs2026/SeekyVS).
$ported = [ordered]@{
    'VSNeo.Seeky/WebUI/index.html'         = 'WebUI/index.html'
    'VSNeo.Seeky/SeekyState.cs'            = 'SeekyState.cs'
    'VSNeo.Seeky/RecentFiles.cs'           = 'RecentFiles.cs'
    'VSNeo.Seeky/SymbolClassifier.cs'      = 'SymbolClassifier.cs'
    'VSNeo.Seeky/SymbolOutline.cs'         = 'SymbolOutline.cs'
    'VSNeo.Seeky/FuzzyMatcher.cs'          = 'FuzzyMatcher.cs'
    'VSNeo.Seeky/SeekyRange.cs'            = 'SeekyRange.cs'
    'VSNeo.Seeky/LineSearch.cs'            = 'BufferSearch.cs'
    'VSNeo.Seeky/SeekyPickerController.cs' = 'SeekyModalWindowManager.cs'
}

& git -C $sub fetch --quiet origin
if ($LASTEXITCODE -ne 0) { throw 'git fetch in external/Seeky failed' }
$pinned = (& git -C $sub rev-parse --short HEAD).Trim()
$target = (& git -C $sub rev-parse --short "$Ref^{commit}").Trim()
if ($LASTEXITCODE -ne 0) { throw "Unknown ref '$Ref' in external/Seeky" }

if ($pinned -eq $target) {
    Write-Host "external/Seeky is at $pinned, already $Ref. Nothing to take."
    exit 0
}

Write-Host "external/Seeky: $pinned -> $Ref ($target)"
& git -C $sub log --oneline "$pinned..$target" -- vs2026/SeekyVS | ForEach-Object { Write-Host "  $_" }

Write-Host ""
Write-Host "To port by hand into VSNeo.Seeky:"
$any = $false
foreach ($entry in $ported.GetEnumerator()) {
    $log = & git -C $sub log --format='%h %cs %s' "$pinned..$target" -- "vs2026/SeekyVS/$($entry.Value)"
    if ($log) {
        $any = $true
        Write-Host "  $($entry.Key)  <-  $($entry.Value)" -ForegroundColor Yellow
        $log | ForEach-Object { Write-Host "      $_" }
    }
}
if (-not $any) { Write-Host "  none" }

if ($Update) {
    & git -C $sub checkout --quiet $target
    if ($LASTEXITCODE -ne 0) { throw "checkout of $target in external/Seeky failed" }
    Write-Host ""
    Write-Host "Moved external/Seeky to $target. Build, then: git add external/Seeky" -ForegroundColor Green
}
exit 0
