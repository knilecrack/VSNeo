#Requires -Version 5.1
<#
.SYNOPSIS
  Builds Lua/vsneo.lua from the modules in Lua/vsneo/.

.DESCRIPTION
  The companion is one Lua chunk: modules share top-level locals, so they are
  concatenated in filename order rather than require()d. The generated file is
  what ships inside the VSIX and what tests/helper.lua loads.

  Edit a module, then run this. Never edit Lua/vsneo.lua directly. The build
  (and CI) run -Check, which fails when the generated file is stale.

.PARAMETER Check
  Verify that Lua/vsneo.lua matches the modules without writing it.
#>
[CmdletBinding()]
param(
    [switch]$Check
)

$ErrorActionPreference = 'Stop'
$dir = Join-Path $PSScriptRoot 'vsneo'
$target = Join-Path $PSScriptRoot 'vsneo.lua'

if (-not (Test-Path $dir)) {
    throw "Missing companion modules: $dir"
}

$files = @(Get-ChildItem -Path $dir -Filter '*.lua' -File | Sort-Object Name)
if ($files.Count -eq 0) {
    throw "No companion modules in $dir"
}

$text = New-Object System.Text.StringBuilder
foreach ($file in $files) {
    [void]$text.Append([System.IO.File]::ReadAllText($file.FullName))
}
$content = $text.ToString()

if ($Check) {
    if (-not (Test-Path $target)) {
        throw "Missing $target; run: pwsh Lua/build-vsneo.ps1"
    }

    if ([System.IO.File]::ReadAllText($target) -ne $content) {
        throw "Lua/vsneo.lua is stale; run: pwsh Lua/build-vsneo.ps1"
    }

    Write-Host "Lua/vsneo.lua is up to date ($($files.Count) modules, $($content.Length) chars)."
    return
}

[System.IO.File]::WriteAllText($target, $content, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "Wrote Lua/vsneo.lua from $($files.Count) modules ($($content.Length) chars)."
