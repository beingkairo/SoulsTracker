# Temporary diagnostics for the Worker test step. Never log process arguments or environment values.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$previousDiagnostics = $env:WORKER_TEST_DIAGNOSTICS
$env:WORKER_TEST_DIAGNOSTICS = '1'
$stop = [Threading.ManualResetEventSlim]::new($false)
$sampler = $null
$testExit = 1
try {
    try {
        $sampler = Start-ThreadJob -StreamingHost $Host -ArgumentList $stop -ScriptBlock {
            param($stop)
            $ErrorActionPreference = 'Stop'
            $paths = [ordered]@{
                cpuPercent = '\Processor(_Total)\% Processor Time'
                availableMiB = '\Memory\Available MBytes'
                diskQueue = '\PhysicalDisk(_Total)\Current Disk Queue Length'
                diskReadSeconds = '\PhysicalDisk(_Total)\Avg. Disk sec/Read'
                diskWriteSeconds = '\PhysicalDisk(_Total)\Avg. Disk sec/Write'
            }
            try {
                Get-Counter -Counter @($paths.Values) -SampleInterval 2 -MaxSamples 180 -ErrorAction Stop | ForEach-Object {
                    if ($stop.IsSet) { throw [OperationCanceledException]::new() }
                    $clock = [Diagnostics.Stopwatch]::StartNew()
                    $cpuBefore = [Diagnostics.Process]::GetCurrentProcess().TotalProcessorTime
                    $sample = $_
                    $record = [ordered]@{ utcMs = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() }
                    foreach ($key in $paths.Keys) {
                        $counter = $sample.CounterSamples | Where-Object { $_.Path.EndsWith($paths[$key], [StringComparison]::OrdinalIgnoreCase) }
                        $record[$key] = $counter.CookedValue
                    }
                    foreach ($name in @('node', 'workerd', 'msedgewebview2', 'msedge')) {
                        try {
                            $processes = @(Get-Process -Name $name -ErrorAction SilentlyContinue)
                            $record[$name] = @{ count = $processes.Count
                                cpuSeconds = ($processes | Measure-Object CPU -Sum).Sum
                                rssMiB = ($processes | Measure-Object WorkingSet64 -Sum).Sum / 1048576 }
                        } catch { $record[$name] = @{ counters = 'unavailable' } }
                    }
                    $record.sampleProcessingMs = $clock.Elapsed.TotalMilliseconds
                    $record.samplerCpuMs = ([Diagnostics.Process]::GetCurrentProcess().TotalProcessorTime - $cpuBefore).TotalMilliseconds
                    Write-Host ('WORKER_RUNNER ' + ($record | ConvertTo-Json -Compress -Depth 3))
                }
            } catch {
                if (-not $stop.IsSet) { Write-Host 'WORKER_RUNNER {"counters":"unavailable"}' }
            }
        }
    } catch { Write-Warning 'Worker resource sampler unavailable; tests will still run.' }
    # The default CI reporter suppresses passing-test console records.
    # Use the native Windows shim so PowerShell preserves the '--' argument separator.
    npm.cmd test --prefix cloud_overlay -- --reporter=verbose
    $testExit = $LASTEXITCODE
} finally {
    $stop.Set()
    if ($null -ne $sampler) {
        try {
            $null = $sampler | Wait-Job -Timeout 5
            if ($sampler.State -eq 'Running') { $sampler | Stop-Job }
            $sampler | Receive-Job -InformationAction Ignore -ErrorAction Continue
            $sampler | Remove-Job -Force
        } catch { Write-Warning 'Worker resource sampling did not finish normally.' }
    }
    $stop.Dispose()
    $env:WORKER_TEST_DIAGNOSTICS = $previousDiagnostics
}
exit $testExit
