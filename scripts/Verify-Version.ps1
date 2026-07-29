[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$expectedVersion = & (Join-Path $root "eng\Get-Version.ps1")

$package = Get-Content -Raw -Encoding utf8 (Join-Path $root "web_overlay\package.json") | ConvertFrom-Json
if ($package.version -ne $expectedVersion) {
    throw "web_overlay/package.json version '$($package.version)' must match eng/Version.props version '$expectedVersion'."
}

$packageLockContent = Get-Content -Raw -Encoding utf8 (Join-Path $root "web_overlay\package-lock.json")
$packageLockVersions = [regex]::Matches($packageLockContent, '"version"\s*:\s*"([^"]+)"')
if ($packageLockVersions.Count -lt 2 -or
    $packageLockVersions[0].Groups[1].Value -ne $expectedVersion -or
    $packageLockVersions[1].Groups[1].Value -ne $expectedVersion) {
    throw "web_overlay/package-lock.json versions must match eng/Version.props version '$expectedVersion'."
}

[xml]$versionProps = Get-Content -Raw -Encoding utf8 (Join-Path $root "eng\Version.props")
$properties = $versionProps.Project.PropertyGroup
if ($properties.AssemblyVersion -ne "$expectedVersion.0" -or $properties.FileVersion -ne "$expectedVersion.0") {
    throw "eng/Version.props assembly and file versions must match '$expectedVersion.0'."
}

Write-Output "Version verified: $expectedVersion"
