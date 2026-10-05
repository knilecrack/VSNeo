<#
.SYNOPSIS
    Keeps VSNeo's copies of Seeky (knilecrack/Seeky, vs2026/SeekyVS) honest.

.DESCRIPTION
    Two kinds of copy, checked differently:

    Verbatim - SeekyEngine/Upstream/*.cs and SeekyEngine/Tools/fff_c.dll (+ .sha256).
      seeky-engine.exe compiles these unchanged, so they must match Seeky at the
      commit pinned in SeekyEngine/Upstream/UPSTREAM.md. A mismatch fails (exit 1):
      either someone edited them here, or the pin is stale. -Update copies them
      from -Ref and moves the pin there.

    Ported - VSNeo.Seeky/*: net472 ports with VSNeo's additions, so never
      byte-equal. Reported, not failed: the upstream commits since the pin that
      touched each one, i.e. what still has to be brought across by hand.

    Comparison is by git blob id (line endings normalized the way git stores
    them), so no Seeky checkout of the right commit is needed - any clone that
    has the commits will do.

.EXAMPLE
    ./tools/sync-seeky.ps1 -SeekyPath O:\repos\knilecrack\Seeky
    ./tools/sync-seeky.ps1 -SeekyPath O:\repos\knilecrack\Seeky -Ref origin/master -Update
#>
param(
    # A clone of knilecrack/Seeky. Defaults to $env:SEEKY_PATH.
    [string]$SeekyPath = $env:SEEKY_PATH,

    # The Seeky commit to compare against or update to. Default: the pinned commit
    # for the verbatim check; HEAD of the clone for the drift report.
    [string]$Ref,

    # Copy the verbatim files from -Ref (default HEAD) and re-pin UPSTREAM.md.
    [switch]$Update
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$upstreamDir = 'vs2026/SeekyVS'
$upstreamMd = Join-Path $repo 'SeekyEngine/Upstream/UPSTREAM.md'

if (-not $SeekyPath -or -not (Test-Path (Join-Path $SeekyPath '.git'))) {
    throw "Pass -SeekyPath (or set SEEKY_PATH) to a clone of knilecrack/Seeky."
}

# Local path (relative to this repo) -> path under vs2026/SeekyVS.
$verbatim = [ordered]@{
    'SeekyEngine/Upstream/FffNativeClient.cs'  = 'FffNativeClient.cs'
    'SeekyEngine/Upstream/SymbolIndex.cs'      = 'SymbolIndex.cs'
    'SeekyEngine/Upstream/SymbolClassifier.cs' = 'SymbolClassifier.cs'
    'SeekyEngine/Upstream/FuzzyMatcher.cs'     = 'FuzzyMatcher.cs'
    'SeekyEngine/Upstream/SeekyRange.cs'       = 'SeekyRange.cs'
    'SeekyEngine/Tools/fff_c.dll'              = 'Tools/fff_c.dll'
    'SeekyEngine/Tools/fff_c.dll.sha256'       = 'Tools/fff_c.dll.sha256'
}
$ported = [ordered]@{
    'VSNeo.Seeky/WebUI/index.html'    = 'WebUI/index.html'
    'VSNeo.Seeky/SeekyState.cs'       = 'SeekyState.cs'
    'VSNeo.Seeky/RecentFiles.cs'      = 'RecentFiles.cs'
    'VSNeo.Seeky/SymbolClassifier.cs' = 'SymbolClassifier.cs'
    'VSNeo.Seeky/SymbolOutline.cs'    = 'SymbolOutline.cs'
    'VSNeo.Seeky/FuzzyMatcher.cs'     = 'FuzzyMatcher.cs'
    'VSNeo.Seeky/SeekyRange.cs'       = 'SeekyRange.cs'
    'VSNeo.Seeky/LineSearch.cs'       = 'BufferSearch.cs'
    'VSNeo.Seeky/SeekyPickerController.cs' = 'SeekyModalWindowManager.cs'
}

function Seeky([string[]]$GitArgs) {
    $out = & git -C $SeekyPath @GitArgs 2>$null
    if ($LASTEXITCODE -ne 0) { return $null }
    return $out
}

$pinText = Get-Content $upstreamMd -Raw
if ($pinText -notmatch 'at commit `([0-9a-f]{7,40})`') { throw "No pinned commit in $upstreamMd" }
$pinned = $Matches[1]
if (-not (Seeky @('rev-parse', '--verify', '--quiet', "$pinned^{commit}"))) {
    throw "Pinned commit $pinned is not in $SeekyPath - fetch it first."
}

# ------------------------------------------------------------------ update
if ($Update) {
    $target = if ($Ref) { $Ref } else { 'HEAD' }
    $full = Seeky @('rev-parse', "$target^{commit}")
    if (-not $full) { throw "Unknown ref '$target' in $SeekyPath" }
    $short = $full.Substring(0, 7)
    foreach ($entry in $verbatim.GetEnumerator()) {
        $dest = Join-Path $repo $entry.Key
        # cmd's redirection writes git's bytes untouched - the dll included.
        cmd /c "git -C `"$SeekyPath`" show $full`:$upstreamDir/$($entry.Value) > `"$dest`""
        if ($LASTEXITCODE -ne 0) { throw "Copying $($entry.Value) at $short failed" }
    }
    $subject = Seeky @('log', '-1', '--format=%cs, "%s"', $full)
    # The subject may wrap and may hold parentheses ("fix(library): ..."): up to the first '")'.
    $pinText = $pinText -replace '(?s)at commit `[0-9a-f]+` \(\d{4}-\d{2}-\d{2}, ".*?"\)', "at commit ``$short`` ($subject)"
    Set-Content $upstreamMd $pinText -NoNewline
    Write-Host "Copied $($verbatim.Count) files from Seeky $short and re-pinned UPSTREAM.md."
    $pinned = $short
    $Ref = $null
}

# ------------------------------------------------------------------ verbatim check
$failed = $false
$checkRef = if ($Ref) { $Ref } else { $pinned }
Write-Host "Verbatim copies vs Seeky $checkRef"
foreach ($entry in $verbatim.GetEnumerator()) {
    $local = & git -C $repo hash-object -- $entry.Key
    $theirs = Seeky @('rev-parse', "$checkRef`:$upstreamDir/$($entry.Value)")
    if ($local -eq $theirs) {
        Write-Host "  ok       $($entry.Key)"
    } else {
        Write-Host "  DIFFERS  $($entry.Key)" -ForegroundColor Red
        $failed = $true
    }
}

# The sha256 file is the dll's own claim; hold the dll to it.
$sumLine = (Get-Content (Join-Path $repo 'SeekyEngine/Tools/fff_c.dll.sha256') -TotalCount 1).Trim()
$actual = (Get-FileHash (Join-Path $repo 'SeekyEngine/Tools/fff_c.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
if ($sumLine.Split(' ')[0].ToLowerInvariant() -ne $actual) {
    Write-Host "  DIFFERS  fff_c.dll does not match fff_c.dll.sha256" -ForegroundColor Red
    $failed = $true
}

# ------------------------------------------------------------------ drift report
$head = if ($Ref) { $Ref } else { 'HEAD' }
Write-Host ""
Write-Host "Ported copies: upstream commits since $pinned (to $head) still to bring across"
$anyDrift = $false
foreach ($entry in $ported.GetEnumerator()) {
    $log = Seeky @('log', '--format=%h %cs %s', "$pinned..$head", '--', "$upstreamDir/$($entry.Value)")
    if ($log) {
        $anyDrift = $true
        Write-Host "  $($entry.Key)  <-  $($entry.Value)" -ForegroundColor Yellow
        $log | ForEach-Object { Write-Host "      $_" }
    }
}
$engineLog = Seeky @('log', '--format=%h %cs %s', "$pinned..$head", '--',
    ($verbatim.Values | ForEach-Object { "$upstreamDir/$_" }))
if ($engineLog) {
    $anyDrift = $true
    Write-Host "  SeekyEngine verbatim files (run with -Update to take them):" -ForegroundColor Yellow
    $engineLog | ForEach-Object { Write-Host "      $_" }
}
if (-not $anyDrift) { Write-Host "  none" }

if ($failed) {
    Write-Host ""
    Write-Host "Verbatim copies differ from Seeky $checkRef. Change them in Seeky, then -Update." -ForegroundColor Red
    exit 1
}
exit 0
