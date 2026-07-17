[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$expectedVersion = & (Join-Path $root "eng\Get-Version.ps1")

$package = Get-Content -Raw -Encoding utf8 (Join-Path $root "web_overlay\package.json") | ConvertFrom-Json
if ($package.version -ne $expectedVersion) {
    throw "web_overlay/package.json version '$($package.version)' must match eng/Version.props version '$expectedVersion'."
}

Write-Output "Version verified: $expectedVersion"
