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
# Publication remains pinned to its independently approved source/version until release approval.
$workflowVersionMatch = [regex]::Match($releaseWorkflow, "metadata.Version -ne '([0-9]+\.[0-9]+\.[0-9]+)'")
if (-not $workflowVersionMatch.Success) { throw 'Release workflow is missing its pinned version guard.' }
$workflowVersion = $workflowVersionMatch.Groups[1].Value

$requirements = @(
    @{ Path = $readmePath; Content = $readme; Text = "## Compatible games" },
    @{ Path = $readmePath; Content = $readme; Text = "## Getting started" },
    @{ Path = $readmePath; Content = $readme; Text = "docs/RELEASE-GETTING-STARTED.md" },
    @{ Path = $releaseGuidePath; Content = $releaseGuide; Text = "https://github.com/beingkairo/SoulsTracker#getting-started" },
    @{ Path = $releaseBodyPath; Content = $releaseBody; Text = "# SoulsTracker v$version" },
    @{ Path = $releaseBodyPath; Content = $releaseBody; Text = "## Latest changes" },
    @{ Path = $releaseWorkflowPath; Content = $releaseWorkflow; Text = "Get-ReleaseMetadata.ps1" },
    @{ Path = $releaseWorkflowPath; Content = $releaseWorkflow; Text = "prepared/release-notes.md" },
    @{ Path = $releaseWorkflowPath; Content = $releaseWorkflow; Text = "prepared/SoulsTrackerV$workflowVersion.exe" },
    @{ Path = $releaseWorkflowPath; Content = $releaseWorkflow; Text = "prepared/SHA256SUMS.txt" }
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
if ([version]$version -ge [version]'2.2.0') { $expectedGames += 'Hollow Knight' }
$expectedList = ($expectedGames | ForEach-Object { "- $_" }) -join "`n"
foreach ($document in @(@{ Name = 'README'; Body = $readme }, @{ Name = 'release notes'; Body = $releaseBody })) {
    $match = [regex]::Match($document.Body, '(?ms)^## Compatible games\s*\r?\n(.*?)(?=^## |\z)')
    if (-not $match.Success -or $match.Groups[1].Value.Trim().Replace("`r`n", "`n") -cne $expectedList) {
        throw "$($document.Name) must list exactly the current compatible game names in order."
    }
}
if ($readme -notmatch '(?m)^\[Download the latest release\]\(https://github\.com/beingkairo/SoulsTracker/releases/latest\)\.$' -or
    $readme -notmatch '(?m)^- Global hotkeys$') {
    throw 'README is missing its release link or current hotkey feature.'
}
$expectedChanges = if ($version -eq '2.2.0') {
    '- Added self-contained Hollow Knight tracking for deaths observed while tracking is active, with counts saved across application restarts.'
} elseif ($version -eq '2.1.2') {
    @(
        '- Expanded Bloodborne 1.09 death tracking to compatible PC runtimes, including Mrsuss60 bbport, Lantern Engine and deadinside28 Windows bbport, while preserving existing shadPS4 tracking.',
        '- Compatible Bloodborne PC runtimes are detected independently of their executable filenames, with stable tracking through character changes and game restarts.'
    ) -join "`n"
} elseif ($version -eq '2.1.1') {
    @(
        '- Choose whether to enable Hosted Overlay. Fresh installs start with it disabled; local tracking, Preview and text exports remain available independently.',
        '- Hosted Overlay shows when SoulsTracker is closed or an older client needs an update.',
        '- Hosted Overlay URLs stay the same when updating to this version.',
        '- The update popup opens the SoulsTracker product page.'
    ) -join "`n"
} else {
    @(
        '- Choose whether to enable Hosted Overlay. Fresh installs start with it disabled; local tracking, Preview and text exports remain available independently.',
        '- Choose whether closing the window minimizes SoulsTracker to the tray with the optional Minimize to tray on close setting.',
        '- Fixed Sticky Preview scrollbar stutter while docking and undocking.',
        '- Settings organization and Hosted Overlay disabled-state behavior have been refined.'
    ) -join "`n"
}
$changesMatch = [regex]::Match($releaseBody, '(?ms)^## Latest changes\s*\r?\n(.*?)(?=^## |\z)')
if (-not $changesMatch.Success -or $changesMatch.Groups[1].Value.Trim().Replace("`r`n", "`n") -cne $expectedChanges) {
    throw 'Release notes must contain exactly the approved latest changes.'
}
if ($releaseBody -match '(?m)^## Notes\s*$' -or $releaseBody -match 'Existing local OBS URLs must be replaced\.') {
    throw 'Release notes contain removed disclosure or migration commentary.'
}
$expectedReleaseBody = "# SoulsTracker v$version`n`n## Compatible games`n`n$expectedList`n`n## Latest changes`n`n$expectedChanges"
if ($releaseBody.Trim().Replace("`r`n", "`n") -cne $expectedReleaseBody) {
    throw 'Release notes must match the checked version''s title and two sections.'
}
if ($releaseWorkflow -notmatch '(?m)^\s+generate_release_notes: false\s*$' -or
    $releaseWorkflow -notmatch '(?m)^\s+body_path: prepared/release-notes\.md\s*$' -or
    $releaseWorkflow -match 'Append setup guide|gh api --method PATCH|generate_release_notes: true') {
    throw 'Release workflow must publish only the static release notes without generated or appended prose.'
}

Write-Output "Release guide verified."
