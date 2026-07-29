[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$readmePath = Join-Path $root "README.md"
$releaseGuidePath = Join-Path $root "docs\RELEASE-GETTING-STARTED.md"
$releaseBodyPath = Join-Path $root "docs\releases\v1.2.1.md"
$releaseWorkflowPath = Join-Path $root ".github\workflows\release.yml"

$readme = Get-Content -Raw -Encoding utf8 $readmePath
$releaseGuide = Get-Content -Raw -Encoding utf8 $releaseGuidePath
$releaseBody = Get-Content -Raw -Encoding utf8 $releaseBodyPath
$releaseWorkflow = Get-Content -Raw -Encoding utf8 $releaseWorkflowPath

$requirements = @(
    @{ Path = $readmePath; Content = $readme; Text = "## Getting started" },
    @{ Path = $readmePath; Content = $readme; Text = "Install SoulsTracker and open it before opening OBS" },
    @{ Path = $readmePath; Content = $readme; Text = "## Disclaimer" },
    @{ Path = $releaseGuidePath; Content = $releaseGuide; Text = "Open SoulsTracker before OBS" },
    @{ Path = $releaseBodyPath; Content = $releaseBody; Text = "SoulsTracker v1.2.1" },
    @{ Path = $releaseBodyPath; Content = $releaseBody; Text = "SoulsTracker is read-only" },
    @{ Path = $releaseWorkflowPath; Content = $releaseWorkflow; Text = "body_path: docs/releases/v1.2.1.md" },
    @{ Path = $releaseWorkflowPath; Content = $releaseWorkflow; Text = "installer/Output/SoulsTrackerV1.2.exe" },
    @{ Path = $releaseWorkflowPath; Content = $releaseWorkflow; Text = "artifacts/SoulsTrackerV1.2-portable.zip" },
    @{ Path = $releaseWorkflowPath; Content = $releaseWorkflow; Text = "artifacts/SoulsTrackerV1.2.sbom.spdx.json" },
    @{ Path = $releaseWorkflowPath; Content = $releaseWorkflow; Text = "artifacts/SHA256SUMS.txt" },
    @{ Path = $releaseWorkflowPath; Content = $releaseWorkflow; Text = "docs/RELEASE-GETTING-STARTED.md" },
    @{ Path = $releaseWorkflowPath; Content = $releaseWorkflow; Text = "Append setup guide to release notes" }
)

foreach ($requirement in $requirements) {
    if (-not $requirement.Content.Contains($requirement.Text)) {
        throw "Expected '$($requirement.Text)' in '$($requirement.Path)'."
    }
}

Write-Output "Release guide verified."
