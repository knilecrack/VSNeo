# Auto-increments the VSIX version before packaging so each install is
# distinguishable and upgrades cleanly over the previous build.
# Bumps the last present component: 1.0.3 -> 1.0.4, 1.0.3.1 -> 1.0.3.2.
param(
    [Parameter(Mandatory = $true)]
    [string]$ManifestPath
)

$xml = New-Object System.Xml.XmlDocument
$xml.PreserveWhitespace = $true
$xml.Load($ManifestPath)

$identity = $xml.PackageManifest.Metadata.Identity
$current = [version]$identity.Version

if ($current.Revision -ge 0) {
    $next = [version]::new($current.Major, $current.Minor, $current.Build, $current.Revision + 1)
} elseif ($current.Build -ge 0) {
    $next = [version]::new($current.Major, $current.Minor, $current.Build + 1)
} else {
    $next = [version]::new($current.Major, $current.Minor + 1)
}

$identity.Version = $next.ToString()
$xml.Save($ManifestPath)
Write-Host "VSIX version bumped: $current -> $next"
