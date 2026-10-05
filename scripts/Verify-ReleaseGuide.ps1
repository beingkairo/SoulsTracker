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
    @{ Path = $readmePath; Content = $readme; Text = "## Compatible games" },
    @{ Path = $readmePath; Content = $readme; Text = "## Start streaming" },
    @{ Path = $readmePath; Content = $readme; Text = "docs/RELEASE-GETTING-STARTED.md" },
    @{ Path = $releaseGuidePath; Content = $releaseGuide; Text = "either startup order is supported" },
    @{ Path = $releaseBodyPath; Content = $releaseBody; Text = "# SoulsTracker v$version" },
    @{ Path = $releaseBodyPath; Content = $releaseBody; Text = "## Latest changes" },
    @{ Path = $releaseBodyPath; Content = $releaseBody; Text = "- Death Counter Overlays are now hosted instead of being local only." },
    @{ Path = $releaseBodyPath; Content = $releaseBody; Text = "- Uninstall now offers a choice to keep or delete local SoulsTracker settings." },
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

$expectedGames = @(
    "Demon's Souls",
    'Dark Souls Remastered',
    'Dark Souls II: Scholar of the First Sin',
    'Dark Souls III',
    'Bloodborne',
    'Sekiro: Shadows Die Twice',
    'Elden Ring',
    'Black Myth: Wukong',
    'Lies of P'
)
$expectedList = ($expectedGames | ForEach-Object { "- $_" }) -join "`n"
foreach ($document in @(@{ Name = 'README'; Body = $readme }, @{ Name = 'release notes'; Body = $releaseBody })) {
    $match = [regex]::Match($document.Body, '(?ms)^## Compatible games\s*\r?\n(.*?)(?=^## |\z)')
    if (-not $match.Success -or $match.Groups[1].Value.Trim().Replace("`r`n", "`n") -cne $expectedList) {
        throw "$($document.Name) must list exactly the nine current compatible game names in order."
    }
}
if ($releaseBody -match '(?m)^## Notes\s*$' -or $releaseBody -match 'Existing local OBS URLs must be replaced\.') {
    throw 'Release notes contain removed disclosure or migration commentary.'
}

Write-Output "Release guide verified."
