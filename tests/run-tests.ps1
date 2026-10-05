#Requires -Version 5.1
<#
.SYNOPSIS
  Runs the companion Lua test suites against a real Neovim, headless.

.DESCRIPTION
  Each tests/*_tests.lua file runs as its own nvim process
  (nvim --headless -u NONE -i NONE -l <file>) from the repo root. HOME and
  USERPROFILE are pointed at a fresh temp dir so a real ~/.vsneorc cannot
  leak into the companion's rc sourcing.

  nvim is located from -NvimPath, then VSNEO_NVIM_PATH, then PATH.
#>
[CmdletBinding()]
param(
  [string]$NvimPath
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

if (-not $NvimPath) { $NvimPath = $env:VSNEO_NVIM_PATH }
if (-not $NvimPath) {
  $cmd = Get-Command nvim -ErrorAction SilentlyContinue
  if ($cmd) { $NvimPath = $cmd.Source }
}
if (-not $NvimPath -or -not (Test-Path $NvimPath)) {
  Write-Error 'nvim not found; install Neovim, set VSNEO_NVIM_PATH, or pass -NvimPath'
}

$fakeHome = Join-Path ([IO.Path]::GetTempPath()) ('vsneo-testhome-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fakeHome | Out-Null

$env:HOME = $fakeHome
$env:USERPROFILE = $fakeHome
$env:XDG_CONFIG_HOME = $fakeHome

$failed = 0
$ran = 0
# A wedged nvim (a search loop, a blocked prompt) used to hang the CI job
# until its own timeout; two minutes is generous for any suite here.
$timeoutSeconds = 120
Push-Location $root
try {
  foreach ($test in Get-ChildItem (Join-Path $PSScriptRoot '*_tests.lua')) {
    $ran++
    $proc = Start-Process -FilePath $NvimPath -NoNewWindow -PassThru `
      -ArgumentList @('--headless', '-u', 'NONE', '-i', 'NONE', '-l', $test.FullName)
    if (-not $proc.WaitForExit($timeoutSeconds * 1000)) {
      try { $proc.Kill() } catch {}
      $failed++
      Write-Output "FAIL $($test.Name) (timed out after ${timeoutSeconds}s)"
      continue
    }
    if ($proc.ExitCode -eq 0) {
      Write-Output "PASS $($test.Name)"
    } else {
      $failed++
      Write-Output "FAIL $($test.Name)"
    }
  }
} finally {
  Pop-Location
  Remove-Item -Recurse -Force $fakeHome -ErrorAction SilentlyContinue
}

if ($ran -eq 0) {
  # Green with nothing run is how a moved directory or a renamed glob goes
  # unnoticed for months.
  Write-Output 'no *_tests.lua suites found'
  exit 1
}
if ($failed -gt 0) {
  Write-Output "$failed suite(s) failed"
  exit 1
}
Write-Output "all $ran suites passed"
