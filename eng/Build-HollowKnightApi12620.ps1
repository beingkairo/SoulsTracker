[CmdletBinding()]
param([Parameter(Mandatory)][string]$VanillaManagedPath)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'This verified build recipe targets Windows.' }
$root = Split-Path -Parent $PSScriptRoot
$profile = Get-Content -Raw (Join-Path $root 'src/SoulsTracker.Infrastructure/HollowKnightApi12620.json') | ConvertFrom-Json -AsHashtable
if ((& dotnet --version).Trim() -ne '10.0.301') { throw 'This verified build recipe requires .NET SDK 10.0.301.' }
$inputDirectory = [IO.Path]::GetFullPath($VanillaManagedPath)
$output = [IO.Path]::GetFullPath((Join-Path $root 'plugins/Soulstracker.DeathTracker/obj/api-12620'))

function Assert-LocalPath([string]$Path) {
    if ($Path.StartsWith('\\')) { throw 'Network paths are unsupported.' }
    $cursor = $Path
    while ($cursor) {
        if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Linked build paths are unsupported.' }
        $cursor = Split-Path -Parent $cursor
    }
}
Assert-LocalPath $inputDirectory
Assert-LocalPath $output
if ($output.StartsWith($inputDirectory + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Output cannot be inside the game references.' }
foreach ($name in $profile.VanillaFiles.Keys) {
    $file = Join-Path $inputDirectory $name
    Assert-LocalPath $file
    if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or (Get-FileHash -LiteralPath $file).Hash -cne $profile.VanillaFiles[$name]) { throw "Clean Steam reference verification failed: $name" }
}

# All source, restore and patching outputs stay under a fresh generated build directory.
$work = Join-Path $root ('obj/hk78/' + [guid]::NewGuid().ToString('N'))
Assert-LocalPath $work
$null = New-Item -ItemType Directory -Path $work -Force
$archive = Join-Path $work 'source.zip'
Invoke-WebRequest -Uri $profile.SourceUrl -OutFile $archive
if ((Get-FileHash -LiteralPath $archive).Hash -cne $profile.SourceSha256) { throw 'Upstream source archive verification failed.' }
Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $work 'source')
$extracted = Join-Path $work ('source/api-' + $profile.SourceRevision)
# Keep legacy patcher paths below Windows' legacy path limit.
Rename-Item -LiteralPath $extracted -NewName 's'
$source = Join-Path $work 'source/s'
$vanilla = Join-Path $source 'Vanilla'
$null = New-Item -ItemType Directory -Path $vanilla
foreach ($name in $profile.VanillaFiles.Keys) {
    $copy = Join-Path $vanilla $name
    Copy-Item -LiteralPath (Join-Path $inputDirectory $name) -Destination $copy
    if ((Get-FileHash -LiteralPath $copy).Hash -cne $profile.VanillaFiles[$name]) { throw 'Reference files changed during copying.' }
}

function Replace-ExactlyOnce([string]$File, [string]$Before, [string]$After) {
    $body = [IO.File]::ReadAllText($File).Replace("`r`n", "`n")
    if ([regex]::Matches($body, [regex]::Escape($Before)).Count -ne 1) { throw 'The reviewed build correction no longer matches upstream source.' }
    [IO.File]::WriteAllText($File, $body.Replace($Before, $After), [Text.UTF8Encoding]::new($false))
}
# Build-only corrections: stable hook MVID/timestamp, and no generated PDBs or local paths.
Replace-ExactlyOnce (Join-Path $source 'MonoMod.RuntimeDetour.HookGen/Program.cs') 'mOut.Write(pathOut);' 'mOut.Write(pathOut, new WriterParameters { DeterministicMvid = true, Timestamp = 0 });'
$patcher = Join-Path $source 'MonoMod.Patcher/MonoModder.cs'
Replace-ExactlyOnce $patcher 'WriteSymbols = true,' 'WriteSymbols = false,'
$provider = @'
                        SymbolWriterProvider =
#if !CECIL0_9
                            pdb ? new NativePdbWriterProvider() :
#else
                            pdb ? new PdbWriterProvider() :
#endif
                            mdb ? new MdbWriterProvider() :
                            (ISymbolWriterProvider)null
'@
Replace-ExactlyOnce $patcher $provider '                        SymbolWriterProvider = null'
foreach ($project in @('PrePatcher', 'PostPatcher', 'MonoMod.Patcher', 'MonoMod.RuntimeDetour.HookGen', 'TeamCherry.Localization', 'Assembly-CSharp')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "HollowKnightApi12620/locks/$project.json") -Destination (Join-Path $source "$project/packages.lock.json") -Force
}
$apiOutput = Join-Path $work 'api'
$common = @('-p:ImportDirectoryBuildProps=false', '-p:ImportDirectoryBuildTargets=false', '-p:ManagePackageVersionsCentrally=false',
    '-p:RuntimeIdentifiers=win-x64%3Bwin-x86', '-p:RestoreLockedMode=true', '-p:RestorePackagesWithLockFile=true',
    '-p:DebugType=None', '-p:DebugSymbols=false', '-p:Deterministic=true',
    "-p:GamePath=$(Join-Path $work 'never-game')", "-p:CopyDir=$(Join-Path $work 'install-sink')", "-p:OutputDir=$apiOutput", "-p:RestorePackagesPath=$(Join-Path $work 'packages')")
& dotnet msbuild (Join-Path $source 'Assembly-CSharp/Assembly-CSharp.csproj') -t:Restore -p:RuntimeIdentifier=win-x64 @common
if ($LASTEXITCODE -ne 0) { throw 'Locked API dependency restore failed.' }
foreach ($package in Get-ChildItem -LiteralPath (Join-Path $work 'packages') -Recurse -Filter '*.nupkg' -File) {
    & dotnet nuget verify --all $package.FullName
    if ($LASTEXITCODE -ne 0) { throw 'API dependency signature verification failed.' }
}
foreach ($project in @('PrePatcher', 'PostPatcher')) {
    & dotnet build (Join-Path $source "$project/$project.csproj") -c Release -o (Join-Path $source "$project/Output") @common
    if ($LASTEXITCODE -ne 0) { throw "$project build failed." }
}
& dotnet build (Join-Path $source 'Assembly-CSharp/Assembly-CSharp.csproj') -c Release --runtime win-x64 @common
if ($LASTEXITCODE -ne 0) { throw 'Pinned API build failed.' }
foreach ($name in $profile.RuntimeFiles.Keys) {
    $file = Join-Path $apiOutput $name
    if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or (Get-FileHash -LiteralPath $file).Hash -cne $profile.RuntimeFiles[$name]) { throw "API reproduction failed: $name" }
}

Add-Type -AssemblyName System.IO.Compression
$temporary = Join-Path $work 'api-12620.zip'
$zip = [IO.Compression.ZipFile]::Open($temporary, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($name in ($profile.RuntimeFiles.Keys | Sort-Object -CaseSensitive)) {
        $entry = $zip.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
        $stream = $entry.Open()
        try { $bytes = [IO.File]::ReadAllBytes((Join-Path $apiOutput $name)); $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
    }
} finally { $zip.Dispose() }
$destination = Join-Path $output 'api-12620.zip'
$null = New-Item -ItemType Directory -Path $output -Force
if ((Test-Path -LiteralPath $destination) -and (Get-FileHash -LiteralPath $destination).Hash -cne (Get-FileHash -LiteralPath $temporary).Hash) { throw 'A different existing API package was preserved. Review it before replacement.' }
Copy-Item -LiteralPath $temporary -Destination $destination
Write-Output 'Verified API 78 package prepared. No game installation was modified.'
