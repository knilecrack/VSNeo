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
Push-Location $root
try {
  foreach ($test in Get-ChildItem (Join-Path $PSScriptRoot '*_tests.lua')) {
    & $NvimPath --headless -u NONE -i NONE -l $test.FullName
    if ($LASTEXITCODE -eq 0) {
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

if ($failed -gt 0) {
  Write-Output "$failed suite(s) failed"
  exit 1
}
Write-Output 'all suites passed'
