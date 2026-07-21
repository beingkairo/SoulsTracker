[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$readmePath = Join-Path $root "README.md"
$releaseGuidePath = Join-Path $root "docs\RELEASE-GETTING-STARTED.md"
$releaseWorkflowPath = Join-Path $root ".github\workflows\release.yml"

$readme = Get-Content -Raw -Encoding utf8 $readmePath
$releaseGuide = Get-Content -Raw -Encoding utf8 $releaseGuidePath
$releaseWorkflow = Get-Content -Raw -Encoding utf8 $releaseWorkflowPath

$requirements = @(
    @{ Path = $readmePath; Content = $readme; Text = "## Getting started" },
    @{ Path = $readmePath; Content = $readme; Text = "SoulsTracker before OBS" },
    @{ Path = $readmePath; Content = $readme; Text = "## IMPORTANT: Disclaimer" },
    @{ Path = $releaseGuidePath; Content = $releaseGuide; Text = "Open SoulsTracker before OBS" },
    @{ Path = $releaseWorkflowPath; Content = $releaseWorkflow; Text = "docs/RELEASE-GETTING-STARTED.md" },
    @{ Path = $releaseWorkflowPath; Content = $releaseWorkflow; Text = "Append setup guide to release notes" }
)

foreach ($requirement in $requirements) {
    if (-not $requirement.Content.Contains($requirement.Text)) {
        throw "Expected '$($requirement.Text)' in '$($requirement.Path)'."
    }
}

Write-Output "Release guide verified."
