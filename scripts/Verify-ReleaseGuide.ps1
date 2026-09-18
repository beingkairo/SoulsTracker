[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$version = & (Join-Path $root "eng\Get-Version.ps1")
$readmePath = Join-Path $root "README.md"
$releaseGuidePath = Join-Path $root "docs\RELEASE-GETTING-STARTED.md"
$releaseBodyPath = Join-Path $root "docs\releases\v$version.md"
$releaseWorkflowPath = Join-Path $root ".github\workflows\release.yml"

$readme = Get-Content -Raw -Encoding utf8 $readmePath
$releaseGuide = Get-Content -Raw -Encoding utf8 $releaseGuidePath
$releaseBody = Get-Content -Raw -Encoding utf8 $releaseBodyPath
$releaseWorkflow = Get-Content -Raw -Encoding utf8 $releaseWorkflowPath

$requirements = @(
    @{ Path = $readmePath; Content = $readme; Text = "## Games" },
    @{ Path = $readmePath; Content = $readme; Text = "## Start streaming" },
    @{ Path = $readmePath; Content = $readme; Text = "## Privacy and read-only use" },
    @{ Path = $releaseGuidePath; Content = $releaseGuide; Text = "either startup order is supported" },
    @{ Path = $releaseBodyPath; Content = $releaseBody; Text = "SoulsTracker v$version" },
    @{ Path = $releaseBodyPath; Content = $releaseBody; Text = "SoulsTracker is read-only" },
    @{ Path = $releaseWorkflowPath; Content = $releaseWorkflow; Text = "Get-ReleaseMetadata.ps1" },
    @{ Path = $releaseWorkflowPath; Content = $releaseWorkflow; Text = "steps.version.outputs.release_notes" },
    @{ Path = $releaseWorkflowPath; Content = $releaseWorkflow; Text = "steps.version.outputs.artifact_stem" },
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
