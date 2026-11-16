# Auto-increments a VSIX manifest version so local installs upgrade cleanly
# over the previous build.
#
# Builds pass a COPY of the manifest in obj/ (see BumpVsixVersion in
# VSNeo_Extension.csproj), so the tracked source.extension.vsixmanifest stays
# clean. -StatePath remembers the last version handed out, because every build
# starts again from the same source copy.
#
# Bumps the last present component: 1.8.1 -> 1.8.2, 1.8.1.4 -> 1.8.1.5.
param(
    [Parameter(Mandatory = $true)]
    [string]$ManifestPath,

    # Optional: file holding the last version this script produced.
    [string]$StatePath
)

$xml = New-Object System.Xml.XmlDocument
$xml.PreserveWhitespace = $true
$xml.Load($ManifestPath)

$identity = $xml.PackageManifest.Metadata.Identity
$current = [version]$identity.Version
$base = $current

if ($StatePath -and (Test-Path $StatePath)) {
    $stateText = (Get-Content -Path $StatePath -Raw).Trim()
    if ($stateText) {
        $state = [version]$stateText
        if ($state -ge $base) {
            $base = $state
        }
    }
}

if ($base.Revision -ge 0) {
    $next = [version]::new($base.Major, $base.Minor, $base.Build, $base.Revision + 1)
} elseif ($base.Build -ge 0) {
    $next = [version]::new($base.Major, $base.Minor, $base.Build + 1)
} else {
    $next = [version]::new($base.Major, $base.Minor + 1)
}

$identity.Version = $next.ToString()
$xml.Save($ManifestPath)

if ($StatePath) {
    Set-Content -Path $StatePath -Value $next.ToString() -NoNewline
}

Write-Host "VSIX version bumped: $base -> $next"
