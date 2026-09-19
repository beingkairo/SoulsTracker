[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$EventName,
    [Parameter(Mandatory)] [string]$EventRef,
    [Parameter(Mandatory)] [string]$ExpectedCommit
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$metadata = & (Join-Path $root "eng/Get-ReleaseMetadata.ps1")
$tag = "v$($metadata.Version)"

if ($EventName -cnotin @('push', 'workflow_dispatch')) {
    throw "Unsupported release event."
}
if ($EventRef -cne "refs/tags/$tag") {
    throw "Release ref must be the existing version tag refs/tags/$tag."
}
if ($ExpectedCommit -notmatch '^[0-9a-fA-F]{40}$') {
    throw "Expected commit must be a full commit SHA."
}

function Resolve-Commit([string]$Revision) {
    $resolved = @(& git -C $root rev-parse --verify --end-of-options "${Revision}^{commit}" 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to resolve required release commit."
    }
    if ($resolved.Count -ne 1 -or [string]$resolved[0] -notmatch '^[0-9a-fA-F]{40}$') {
        throw "Git returned an invalid release commit SHA."
    }
    return [string]$resolved[0]
}

$commit = Resolve-Commit $ExpectedCommit
if ((Resolve-Commit 'HEAD') -ne $commit) {
    throw "Checkout HEAD does not match the event commit."
}
if ((Resolve-Commit $EventRef) -ne $commit) {
    throw "Release tag commit does not match the event commit."
}

Write-Output $tag
