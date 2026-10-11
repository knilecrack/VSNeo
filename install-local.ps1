#Requires -Version 5.1
<#
.SYNOPSIS
  Builds this checkout's VSIX and installs it into your regular Visual Studio
  (not the F5 experimental instance), to test it the way users run it.

.DESCRIPTION
  Builds with MSBuild (the pre-build step bumps the VSIX version, so the
  install upgrades over the previous one), installs with each instance's own
  VSIXInstaller, then runs `devenv /updateconfiguration` - VSIXInstaller alone
  does not always make Visual Studio rescan its extensions, and you would test
  the old build without noticing.

  Visual Studio must be closed: VSIXInstaller cannot replace a loaded extension.

.PARAMETER Configuration
  Release (default) or Debug. Debug enables VSNEO_TRACE_KEYS logging.

.PARAMETER Instance
  Only install into instances whose name, path or instance id contains this
  text (for example 2022, Insiders, 04e1babd). Default: every installed
  Visual Studio 17.14 or later.

.PARAMETER NoBuild
  Install the VSIX already built for -Configuration instead of rebuilding.

.EXAMPLE
  .\install-local.ps1
  .\install-local.ps1 -Instance 2022 -Configuration Debug
#>
[CmdletBinding()]
param(
  [ValidateSet('Release', 'Debug')]
  [string]$Configuration = 'Release',
  [string]$Instance,
  [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) { throw "vswhere.exe not found at $vswhere - is Visual Studio installed?" }

$instances = & $vswhere -all -prerelease -version '[17.14,' -format json | ConvertFrom-Json
if ($Instance) {
  $instances = @($instances | Where-Object {
    "$($_.displayName) $($_.installationPath) $($_.instanceId)" -like "*$Instance*"
  })
}
if (-not $instances) { throw "No Visual Studio 17.14+ instance matches '$Instance'." }

if (Get-Process devenv -ErrorAction SilentlyContinue) {
  throw 'Visual Studio is running. Close every instance first - VSIXInstaller cannot replace a loaded extension.'
}

if (-not $NoBuild) {
  $msbuild = & $vswhere -latest -prerelease -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' |
    Select-Object -First 1
  if (-not $msbuild) { throw 'MSBuild not found - install the Visual Studio extension development workload.' }

  & $msbuild (Join-Path $PSScriptRoot 'VSNeo.slnx') -restore "-p:Configuration=$Configuration" -v:minimal -nologo
  if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)." }
}

$vsix = Join-Path $PSScriptRoot "VSNeo_Extension\bin\$Configuration\net472\VSNeo_Extension.vsix"
if (-not (Test-Path $vsix)) { throw "No VSIX at $vsix - build first (drop -NoBuild)." }

$manifest = [xml](Get-Content (Join-Path $PSScriptRoot 'VSNeo_Extension\source.extension.vsixmanifest'))
Write-Host "VSNeo $($manifest.PackageManifest.Metadata.Identity.Version) ($Configuration)"

foreach ($vs in $instances) {
  $ide = Join-Path $vs.installationPath 'Common7\IDE'
  Write-Host "Installing into $($vs.displayName) $($vs.installationVersion) [$($vs.instanceId)]..."

  $p = Start-Process (Join-Path $ide 'VSIXInstaller.exe') -Wait -PassThru `
         -ArgumentList '/quiet', "/instanceIds:$($vs.instanceId)", "`"$vsix`""
  # 1001: this exact version is already installed - still counts as installed.
  if ($p.ExitCode -ne 0 -and $p.ExitCode -ne 1001) {
    throw "VSIXInstaller failed with exit code $($p.ExitCode); see the dd_VSIXInstaller_*.log files in $env:TEMP."
  }

  Start-Process (Join-Path $ide 'devenv.exe') -ArgumentList '/updateconfiguration' -Wait
}

Write-Host 'Done. Start Visual Studio; Extensions > Manage Extensions should show the version above.'
