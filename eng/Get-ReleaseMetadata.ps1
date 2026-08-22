[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

$version = & (Join-Path $PSScriptRoot "Get-Version.ps1")

[pscustomobject]@{
    Version = $version
    ArtifactStem = "SoulsTrackerV$version"
    ReleaseNotesPath = "docs/releases/v$version.md"
}
