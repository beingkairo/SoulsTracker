[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$powerShell = (Get-Process -Id $PID).Path
$version = & (Join-Path $root "eng\Get-Version.ps1")
$fixture = Join-Path ([System.IO.Path]::GetTempPath()) ("SoulsTracker-release-guide-" + [guid]::NewGuid().ToString("N"))
$null = New-Item -ItemType Directory -Path $fixture

try {
    # Copy only the public inputs needed by the actual verifier.
    foreach ($relativePath in @(
        "scripts\Verify-ReleaseGuide.ps1",
        "eng\Get-Version.ps1",
        "eng\Version.props",
        "README.md",
        "docs\RELEASE-GETTING-STARTED.md",
        "docs\releases\v$version.md",
        ".github\workflows\release.yml"
    )) {
        $destination = Join-Path $fixture $relativePath
        $null = New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force
        Copy-Item -LiteralPath (Join-Path $root $relativePath) -Destination $destination
    }

    $verifier = Join-Path $fixture "scripts\Verify-ReleaseGuide.ps1"
    $guidePath = Join-Path $fixture "docs\RELEASE-GETTING-STARTED.md"
    $currentStatement = "either startup order is supported"
    $guide = [System.Text.Encoding]::UTF8.GetString([System.IO.File]::ReadAllBytes($guidePath))
    if (-not $guide.Contains($currentStatement)) {
        throw "Current guide is missing the startup-order statement required by this test."
    }

    foreach ($case in @("current", "obsolete")) {
        if ($case -eq "obsolete") {
            # Preserve every other byte of the guide, including any UTF-8 BOM.
            $obsoleteGuide = $guide.Replace($currentStatement, "Open SoulsTracker before OBS")
            [System.IO.File]::WriteAllBytes($guidePath, [System.Text.Encoding]::UTF8.GetBytes($obsoleteGuide))
        }

        $stdout = Join-Path $fixture "$case.stdout.txt"
        $stderr = Join-Path $fixture "$case.stderr.txt"
        $process = Start-Process -FilePath $powerShell -ArgumentList @(
            "-NoProfile", "-NonInteractive", "-File", "`"$verifier`""
        ) -Wait -PassThru -NoNewWindow -RedirectStandardOutput $stdout -RedirectStandardError $stderr
        $output = (Get-Content -Raw $stdout) + (Get-Content -Raw $stderr)
        Write-Output "$case fixture: exit $($process.ExitCode)"
        Write-Output $output

        if ($case -eq "current") {
            if ($process.ExitCode -ne 0 -or -not $output.Contains("Release guide verified.")) {
                throw "Current hosted guide must pass the actual release-guide verifier."
            }
        } elseif ($process.ExitCode -eq 0 -or -not $output.Contains("Expected '$currentStatement' in '")) {
            throw "Obsolete-only guidance must fail for the missing current startup-order statement."
        }
    }

    Write-Output "Release guide characterization passed."
} finally {
    Remove-Item -LiteralPath $fixture -Recurse -Force
}
