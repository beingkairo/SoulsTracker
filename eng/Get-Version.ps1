[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

[xml]$versionProps = Get-Content -Raw -Encoding utf8 (Join-Path $PSScriptRoot "Version.props")
$properties = $versionProps.Project.PropertyGroup
$prefix = [string]$properties.VersionPrefix
$suffix = [string]$properties.VersionSuffix

if ([string]::IsNullOrWhiteSpace($prefix)) {
    throw "eng/Version.props must define VersionPrefix."
}

if ([string]::IsNullOrWhiteSpace($suffix)) {
    Write-Output $prefix
} else {
    Write-Output "$prefix-$suffix"
}
