[CmdletBinding()]
param(
    [switch]$SkipTests,
    [switch]$SkipInstaller,
    [string]$SourceRoot,
    [string]$OutputRoot,
    [string]$HollowKnightApiPackage,
    [string]$InstallerCompiler,
    [switch]$LocalQualification
)

$ErrorActionPreference = "Stop"
$controlRoot = Split-Path -Parent $PSScriptRoot
$root = if ($SourceRoot) { [IO.Path]::GetFullPath($SourceRoot) } else { $controlRoot }
$solution = Join-Path $root "SoulsTracker.sln"
$artifactsRoot = if ($OutputRoot) { [IO.Path]::GetFullPath($OutputRoot) } else { Join-Path $root "artifacts" }
$stagingRoot = Join-Path $artifactsRoot "staging"
$publishPath = Join-Path $stagingRoot "desktop"
$releasePath = Join-Path $artifactsRoot "desktop"
$webView2StagingPath = Join-Path $stagingRoot "webview2"
$installerOutput = if ($OutputRoot) { Join-Path $artifactsRoot 'installer' } else { Join-Path $root 'installer/Output' }
$overlayPath = Join-Path $root "web_overlay"
$version = & (Join-Path $root "eng\Get-Version.ps1")

if ($LocalQualification -and -not $OutputRoot) {
    throw 'Local qualification requires an explicit fresh OutputRoot.'
}

function Assert-UnlinkedPath([string]$Path) {
    $cursor = [IO.Path]::GetFullPath($Path)
    if ($cursor.StartsWith('\\')) { throw 'Network release paths are unsupported.' }
    while ($cursor) {
        if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Linked release paths are unsupported.'
        }
        $cursor = Split-Path -Parent $cursor
    }
}

if ($OutputRoot) {
    Assert-UnlinkedPath $artifactsRoot
    if (Test-Path -LiteralPath $artifactsRoot) { throw 'An isolated release output root must not already exist.' }
    if ($artifactsRoot -eq $root -or $root.StartsWith($artifactsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'An isolated release output root cannot contain the source checkout.'
    }
    foreach ($protected in @((Join-Path $root 'artifacts'), (Join-Path $controlRoot 'artifacts'), (Join-Path $root 'installer/Output'))) {
        if ($artifactsRoot -eq $protected -or $artifactsRoot.StartsWith($protected + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'An isolated release output root cannot be inside historical artifact directories.'
        }
    }
}
Assert-UnlinkedPath $root

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
& (Join-Path $PSScriptRoot "Verify-ReleaseGuide.ps1")
& (Join-Path $PSScriptRoot "Test-WebView2Installer.ps1")
if ([version]$version -ge [version]'2.2.1') {
    $sourceMode = if ($LocalQualification) { 'source-local' } else { 'source' }
    Invoke-External python @('-B', (Join-Path $PSScriptRoot 'verify_publication.py'), $sourceMode, '--source', $root)
    if (-not $HollowKnightApiPackage) { throw 'An explicit verified Hollow Knight API v78 package is required.' }
    Invoke-External python @('-B', (Join-Path $PSScriptRoot 'verify_hollow_knight_input.py'), '--package', $HollowKnightApiPackage,
        '--profile', (Join-Path $root 'src/SoulsTracker.Infrastructure/HollowKnightApi12620.json'))
    $apiDestination = Join-Path $root 'plugins/Soulstracker.DeathTracker/obj/api-12620/api-12620.zip'
    if ([IO.Path]::GetFullPath($HollowKnightApiPackage).StartsWith((Split-Path $apiDestination) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The explicit API input must be staged outside generated build outputs.'
    }
    Assert-UnlinkedPath $apiDestination
    if ((Test-Path -LiteralPath $apiDestination) -and (Get-FileHash -LiteralPath $apiDestination).Hash -cne (Get-FileHash -LiteralPath $HollowKnightApiPackage).Hash) {
        throw 'A different existing API package was preserved.'
    }
    if ([IO.Path]::GetFullPath($HollowKnightApiPackage) -ne [IO.Path]::GetFullPath($apiDestination)) {
        $null = New-Item -ItemType Directory -Path (Split-Path $apiDestination) -Force
        Copy-Item -LiteralPath $HollowKnightApiPackage -Destination $apiDestination
    }
    Invoke-External python @('-B', (Join-Path $PSScriptRoot 'verify_hollow_knight_input.py'), '--package', $apiDestination,
        '--profile', (Join-Path $root 'src/SoulsTracker.Infrastructure/HollowKnightApi12620.json'))
}
Initialize-CleanStagingDirectory -Path $publishPath -AllowedRoot $stagingRoot
Invoke-External dotnet @("restore", $solution, "--locked-mode")
Invoke-External npm @("ci", "--prefix", $overlayPath)
Invoke-External npm @("ci", "--prefix", (Join-Path $root "cloud_overlay"))
Invoke-External npm @("exec", "--prefix", $overlayPath, "playwright", "install", "chromium")
Invoke-External npm @("run", "build", "--prefix", $overlayPath)
Invoke-External npm @("run", "check", "--prefix", $overlayPath)
Invoke-External npm @("test", "--prefix", $overlayPath, "--", "--workers=1")
Invoke-External npm @("run", "check", "--prefix", (Join-Path $root "cloud_overlay"))
Invoke-External npm @("run", "build", "--prefix", (Join-Path $root "cloud_overlay"))
Invoke-External npm @("test", "--prefix", (Join-Path $root "cloud_overlay"))
Invoke-External dotnet @("format", $solution, "--no-restore", "--verify-no-changes")
Invoke-External dotnet @(
    "build",
    $solution,
    "--configuration",
    "Release",
    "--no-restore",
    "--no-incremental",
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    '-p:CopyOutputSymbolsToPublishDirectory=false'
)

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
    $publishPath,
    "--no-build",
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    '-p:CopyOutputSymbolsToPublishDirectory=false'
)

& (Join-Path $PSScriptRoot 'Export-ThirdPartyNotices.ps1') -PayloadPath $publishPath -SourceRoot $root

Promote-VerifiedDesktopArtifact -StagingPath $publishPath -ReleasePath $releasePath -ArtifactsRoot $artifactsRoot

if (-not $SkipInstaller) {
    $iscc = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
    $isccPath = if ($InstallerCompiler) { [IO.Path]::GetFullPath($InstallerCompiler) }
    elseif ($null -ne $iscc) {
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

    Initialize-CleanStagingDirectory -Path $webView2StagingPath -AllowedRoot $stagingRoot
    try {
        $webView2BootstrapperPath = Join-Path $webView2StagingPath "MicrosoftEdgeWebview2Setup.exe"
        $webView2Bootstrapper = & (Join-Path $PSScriptRoot "Get-WebView2Bootstrapper.ps1") -DestinationPath $webView2BootstrapperPath

        Invoke-External $isccPath @(
            "/O$installerOutput",
            "/DAppVersion=$version",
            "/DBuildOutput=$releasePath",
            "/DWebView2Bootstrapper=$($webView2Bootstrapper.Path)",
            "/DWebView2BootstrapperSha256=$($webView2Bootstrapper.Sha256)",
            (Join-Path $root "installer\SoulsTracker.iss")
        )

        $packagedSourceHash = (Get-FileHash -LiteralPath $webView2Bootstrapper.Path -Algorithm SHA256).Hash
        if ($packagedSourceHash -cne $webView2Bootstrapper.Sha256) {
            throw "The WebView2 bootstrapper changed after signature verification."
        }
    }
    finally {
        if (Test-Path -LiteralPath $webView2StagingPath) {
            Remove-Item -LiteralPath $webView2StagingPath -Recurse -Force
        }
    }
}
