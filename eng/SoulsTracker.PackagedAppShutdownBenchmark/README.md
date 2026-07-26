# Packaged shutdown benchmark

This Windows-only benchmark measures the graceful close path of the
self-contained desktop payload. Its primary scenario opens the embedded overlay
preview, connects a second browser-style WebSocket client, closes the main
window, and waits for the package process tree to exit.

Build the verified self-contained payload:

```powershell
./scripts/Build-Release.ps1 -SkipInstaller
```

Run the default benchmark (one warm-up and ten measured iterations):

```powershell
./scripts/Measure-PackagedShutdown.ps1
```

The default JSON result is written to
`artifacts/benchmarks/packaged-shutdown.json`. The command exits unsuccessfully
if a correctness check fails or if the median exceeds 1.25 seconds, p95 exceeds
2 seconds, or maximum exceeds 3 seconds.

Use parameters for a smaller local smoke run or a different ignored output
file:

```powershell
./scripts/Measure-PackagedShutdown.ps1 `
    -Warmup 0 `
    -Iterations 1 `
    -OutputPath ./artifacts/benchmarks/packaged-shutdown-smoke.json
```

Close any running SoulsTracker instance before starting the benchmark because
the packaged application uses its normal single-instance protection.
