[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$expectedVersion = & (Join-Path $root "eng\Get-Version.ps1")

if ($expectedVersion -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$') {
    throw "eng/Version.props must define a semantic VersionPrefix/VersionSuffix combination."
}

$package = Get-Content -Raw -Encoding utf8 (Join-Path $root "web_overlay\package.json") | ConvertFrom-Json
if ($package.version -ne '0.0.0') {
    throw "web_overlay/package.json is an internal private package and must retain version '0.0.0', not the product version."
}

$packageLockContent = Get-Content -Raw -Encoding utf8 (Join-Path $root "web_overlay\package-lock.json")
$packageLockVersions = [regex]::Matches($packageLockContent, '"version"\s*:\s*"([^"]+)"')
if ($packageLockVersions.Count -lt 2 -or
    $packageLockVersions[0].Groups[1].Value -ne '0.0.0' -or
    $packageLockVersions[1].Groups[1].Value -ne '0.0.0') {
    throw "web_overlay/package-lock.json must retain the internal private package version '0.0.0'."
}

[xml]$versionProps = Get-Content -Raw -Encoding utf8 (Join-Path $root "eng\Version.props")
$properties = $versionProps.Project.PropertyGroup
if ($properties.AssemblyVersion -ne '$(VersionPrefix).0' -or $properties.FileVersion -ne '$(VersionPrefix).0' -or $properties.InformationalVersion -ne '$(Version)') {
    throw "eng/Version.props must derive assembly, file, and informational versions from the single product version."
}

Write-Output "Version verified: $expectedVersion"
