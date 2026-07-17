[CmdletBinding()]
param(
    [switch]$SkipTests,
    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $root "SoulsTracker.sln"
$artifactsRoot = Join-Path $root "artifacts"
$stagingRoot = Join-Path $root "artifacts\staging"
$publishPath = Join-Path $stagingRoot "desktop"
$releasePath = Join-Path $artifactsRoot "desktop"
$overlayPath = Join-Path $root "web_overlay"
$version = & (Join-Path $root "eng\Get-Version.ps1")

function Invoke-External {
    param(
        [Parameter(Mandatory)] [string]$FilePath,
        [Parameter(Mandatory)] [string[]]$Arguments
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath failed with exit code $LASTEXITCODE."
    }
}

function Initialize-CleanStagingDirectory {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string]$AllowedRoot
    )

    $separatorChars = [char[]]@(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar
    )
    $fullAllowedRoot = [System.IO.Path]::GetFullPath($AllowedRoot).TrimEnd($separatorChars) + [System.IO.Path]::DirectorySeparatorChar
    $fullPath = [System.IO.Path]::GetFullPath($Path)

    if (-not $fullPath.StartsWith($fullAllowedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to reset a staging path outside the artifact staging root: $fullPath"
    }

    if (Test-Path -LiteralPath $fullPath) {
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }

    New-Item -ItemType Directory -Path $fullPath -Force | Out-Null
}

function Promote-VerifiedDesktopArtifact {
    param(
        [Parameter(Mandatory)] [string]$StagingPath,
        [Parameter(Mandatory)] [string]$ReleasePath,
        [Parameter(Mandatory)] [string]$ArtifactsRoot
    )

    $separatorChars = [char[]]@(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar
    )
    $fullArtifactsRoot = [System.IO.Path]::GetFullPath($ArtifactsRoot).TrimEnd($separatorChars) + [System.IO.Path]::DirectorySeparatorChar
    $fullStagingPath = [System.IO.Path]::GetFullPath($StagingPath)
    $fullReleasePath = [System.IO.Path]::GetFullPath($ReleasePath)

    if (-not $fullStagingPath.StartsWith($fullArtifactsRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
        -not $fullReleasePath.StartsWith($fullArtifactsRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to promote an artifact outside the artifacts root."
    }

    $stagedExecutable = Join-Path $fullStagingPath "SoulsTracker.Desktop.exe"
    if (-not (Test-Path -LiteralPath $stagedExecutable -PathType Leaf)) {
        throw "The staged desktop publish is incomplete: SoulsTracker.Desktop.exe is missing."
    }

    $backupPath = $null
    if (Test-Path -LiteralPath $fullReleasePath) {
        $backupPath = Join-Path $fullArtifactsRoot ("desktop.previous-" + [DateTime]::UtcNow.ToString("yyyyMMddHHmmssfff"))
        Move-Item -LiteralPath $fullReleasePath -Destination $backupPath -ErrorAction Stop
    }

    try {
        Move-Item -LiteralPath $fullStagingPath -Destination $fullReleasePath -ErrorAction Stop
    }
    catch {
        if ($null -ne $backupPath -and (Test-Path -LiteralPath $backupPath) -and -not (Test-Path -LiteralPath $fullReleasePath)) {
            Move-Item -LiteralPath $backupPath -Destination $fullReleasePath -ErrorAction SilentlyContinue
        }

        throw
    }
}

& (Join-Path $PSScriptRoot "Verify-Version.ps1")
Initialize-CleanStagingDirectory -Path $publishPath -AllowedRoot $stagingRoot
Invoke-External dotnet @("restore", $solution, "--locked-mode")
Invoke-External npm @("ci", "--prefix", $overlayPath)
Invoke-External npm @("exec", "--prefix", $overlayPath, "playwright", "install", "chromium")
Invoke-External npm @("run", "build", "--prefix", $overlayPath)
Invoke-External npm @("run", "check", "--prefix", $overlayPath)
Invoke-External npm @("test", "--prefix", $overlayPath)
Invoke-External dotnet @("format", $solution, "--no-restore", "--verify-no-changes")
Invoke-External dotnet @("build", $solution, "--configuration", "Release", "--no-restore")

if (-not $SkipTests) {
    Invoke-External dotnet @("test", $solution, "--configuration", "Release", "--no-build")
}

Invoke-External dotnet @(
    "publish",
    (Join-Path $root "src\SoulsTracker.Desktop\SoulsTracker.Desktop.csproj"),
    "--configuration",
    "Release",
    "--no-restore",
    "--output",
    $publishPath
)

Promote-VerifiedDesktopArtifact -StagingPath $publishPath -ReleasePath $releasePath -ArtifactsRoot $artifactsRoot

if (-not $SkipInstaller) {
    $iscc = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
    $isccPath = if ($null -ne $iscc) {
        $iscc.Source
    }
    else {
        $innoCandidates = @(
            (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)) "Inno Setup 6\ISCC.exe"),
            (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)) "Inno Setup 6\ISCC.exe")
        )

        $innoCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    }

    if ([string]::IsNullOrWhiteSpace($isccPath)) {
        throw "Inno Setup's ISCC.exe is required for installer packaging. Use -SkipInstaller only for a publish smoke check."
    }

    Invoke-External $isccPath @(
        "/DAppVersion=$version",
        "/DBuildOutput=$releasePath",
        (Join-Path $root "installer\SoulsTracker.iss")
    )
}
