[CmdletBinding()]
param(
    [string]$PublishPath,
    [ValidateRange(0, 100)] [int]$Warmup = 1,
    [ValidateRange(1, 100)] [int]$Iterations = 10,
    [ValidateSet("PreviewAndObs")] [string]$Scenario = "PreviewAndObs",
    [ValidateRange(1, 120)] [int]$TimeoutSeconds = 10,
    [string]$OutputPath
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($PublishPath)) {
    $PublishPath = Join-Path $root "artifacts\desktop"
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $root "artifacts\benchmarks\packaged-shutdown.json"
}

$fullPublishPath = [System.IO.Path]::GetFullPath($PublishPath)
$fullOutputPath = [System.IO.Path]::GetFullPath($OutputPath)
$project = Join-Path $root "eng\SoulsTracker.PackagedAppShutdownBenchmark\SoulsTracker.PackagedAppShutdownBenchmark.csproj"

& dotnet run --project $project --configuration Release -- `
    --publish-path $fullPublishPath `
    --output-path $fullOutputPath `
    --warmup $Warmup `
    --iterations $Iterations `
    --scenario $Scenario `
    --timeout-seconds $TimeoutSeconds

if ($LASTEXITCODE -ne 0) {
    throw "The packaged shutdown benchmark failed with exit code $LASTEXITCODE."
}
