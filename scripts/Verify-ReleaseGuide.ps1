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
    @{ Path = $readmePath; Content = $readme; Text = "## Getting started" },
    @{ Path = $readmePath; Content = $readme; Text = "docs/RELEASE-GETTING-STARTED.md" },
    @{ Path = $releaseGuidePath; Content = $releaseGuide; Text = "either startup order is supported" },
    @{ Path = $releaseGuidePath; Content = $releaseGuide; Text = "SoulsTracker#getting-started" },
    @{ Path = $releaseBodyPath; Content = $releaseBody; Text = "# SoulsTracker v$version" },
    @{ Path = $releaseBodyPath; Content = $releaseBody; Text = "## Latest changes" },
    @{ Path = $releaseBodyPath; Content = $releaseBody; Text = "- Death Counter Overlays are now hosted instead of being local only." },
    @{ Path = $releaseBodyPath; Content = $releaseBody; Text = "- Uninstall now offers a choice to keep or delete local SoulsTracker settings." },
    @{ Path = $releaseWorkflowPath; Content = $releaseWorkflow; Text = "Get-ReleaseMetadata.ps1" },
    @{ Path = $releaseWorkflowPath; Content = $releaseWorkflow; Text = "steps.version.outputs.release_notes" },
    @{ Path = $releaseWorkflowPath; Content = $releaseWorkflow; Text = "steps.version.outputs.artifact_stem" },
    @{ Path = $releaseWorkflowPath; Content = $releaseWorkflow; Text = "artifacts/SHA256SUMS.txt" }
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
if ($readme -notmatch '(?m)^\[Download the latest release\]\(https://github\.com/beingkairo/SoulsTracker/releases/latest\)\.$' -or
    $readme -notmatch '(?m)^- Global hotkeys$') {
    throw 'README is missing its release link or current hotkey feature.'
}
$expectedChanges = @(
    '- SoulsTracker now focuses entirely on total deaths. Boss lists and death sounds have been removed.',
    '- Death Counter Overlays are now hosted instead of being local only.',
    '- Improved game and save selection, Overlay customization, and in-app previews.',
    '- Added optional update checks.',
    '- Uninstall now offers a choice to keep or delete local SoulsTracker settings.'
) -join "`n"
$changesMatch = [regex]::Match($releaseBody, '(?ms)^## Latest changes\s*\r?\n(.*?)(?=^## |\z)')
if (-not $changesMatch.Success -or $changesMatch.Groups[1].Value.Trim().Replace("`r`n", "`n") -cne $expectedChanges) {
    throw 'Release notes must contain exactly the approved latest changes.'
}
if ($releaseBody -match '(?m)^## Notes\s*$' -or $releaseBody -match 'Existing local OBS URLs must be replaced\.') {
    throw 'Release notes contain removed disclosure or migration commentary.'
}
$expectedReleaseBody = "# SoulsTracker v$version`n`n## Compatible games`n`n$expectedList`n`n## Latest changes`n`n$expectedChanges"
if ($releaseBody.Trim().Replace("`r`n", "`n") -cne $expectedReleaseBody) {
    throw 'Release notes must be exactly the approved title and two sections.'
}
if ($releaseWorkflow -notmatch '(?m)^\s+generate_release_notes: false\s*$' -or
    $releaseWorkflow -notmatch '(?m)^\s+body_path: \$\{\{ steps\.version\.outputs\.release_notes \}\}\s*$' -or
    $releaseWorkflow -match 'Append setup guide|gh api --method PATCH|generate_release_notes: true') {
    throw 'Release workflow must publish only the static release notes without generated or appended prose.'
}

Write-Output "Release guide verified."
