[CmdletBinding()]
param(
    [string]$PayloadPath,
    [switch]$DefineFixturesOnly
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$distributionFixtureSource = $root
Add-Type -AssemblyName System.Reflection.Metadata

function Assert-Content([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function New-DistributionFixture([string]$Fixture, [string]$PublishPath) {
    $root = $distributionFixtureSource
    # Deliberately small synthetic assets; no generated binary is executed.
    foreach ($relative in @('scripts/Export-ThirdPartyNotices.ps1', 'LICENSE', 'THIRD_PARTY_NOTICES.md', 'docs/THIRD_PARTY_NOTICES.md', 'eng/Get-Version.ps1', 'eng/Version.props', 'src/SoulsTracker.Desktop/packages.lock.json')) {
        $destination = Join-Path $Fixture $relative
        $null = New-Item -ItemType Directory -Path (Split-Path $destination) -Force
        Copy-Item -LiteralPath (Join-Path $root $relative) -Destination $destination
    }
    $null = New-Item -ItemType Directory -Path $PublishPath -Force
    $version = & (Join-Path $root 'eng/Get-Version.ps1')
    $targetName = '.NETCoreApp,Version=v10.0/win-x64'
    $deps = @{ runtimeTarget = @{ name = $targetName }; targets = @{ $targetName = @{} }; libraries = @{} }
    $cache = Join-Path $Fixture 'cache'
    $assets = @{ libraries = @{}; packageFolders = @{ $cache = @{} }; project = @{ frameworks = @{ 'net10.0-windows' = @{ downloadDependencies = @() } } } }
    $lock = Get-Content -Raw (Join-Path $Fixture 'src/SoulsTracker.Desktop/packages.lock.json') | ConvertFrom-Json -AsHashtable
    $restoredAssets = Get-Content -Raw (Join-Path $root 'src/SoulsTracker.Desktop/obj/project.assets.json') | ConvertFrom-Json -AsHashtable
    $restoredWebView2Notice = @($restoredAssets.packageFolders.Keys | ForEach-Object { Join-Path $_ 'microsoft.web.webview2/1.0.4191.47/NOTICE.txt' } | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })[0]
    Assert-Content (-not [string]::IsNullOrWhiteSpace($restoredWebView2Notice)) 'Restored WebView2 notice fixture input is missing.'
    $fixtureWebView2Notice = Join-Path $cache 'microsoft.web.webview2/1.0.4191.47/NOTICE.txt'
    $null = New-Item -ItemType Directory -Path (Split-Path $fixtureWebView2Notice) -Force
    Copy-Item -LiteralPath $restoredWebView2Notice -Destination $fixtureWebView2Notice
    foreach ($name in @('Microsoft.NETCore.App.Runtime.win-x64', 'Microsoft.WindowsDesktop.App.Runtime.win-x64')) {
        $key = "runtimepack.$name/10.0.9"
        $deps.libraries[$key] = @{ type = 'runtimepack' }
        $deps.targets[$targetName][$key] = @{ native = @{ "$name.txt" = @{} } }
        Set-Content (Join-Path $PublishPath "$name.txt") 'synthetic runtime pack asset'
        $source = Join-Path $cache "$($name.ToLowerInvariant())/10.0.9/runtimes/win-x64/native/$name.txt"
        $null = New-Item -ItemType Directory -Path (Split-Path $source) -Force
        Copy-Item (Join-Path $PublishPath "$name.txt") $source
        $assets.project.frameworks['net10.0-windows'].downloadDependencies += @{ name = $name; version = '[10.0.9, 10.0.9]' }
    }
    foreach ($group in $lock.dependencies.Values) {
        foreach ($name in $group.Keys) {
            $entry = $group[$name]
            if ($entry.type -eq 'Project') { continue }
            $key = "$name/$($entry.resolved)"
            $deps.libraries[$key] = @{ type = 'package'; sha512 = "sha512-$($entry.contentHash)" }
            $deps.targets[$targetName][$key] = @{ runtime = @{ "$name.txt" = @{} } }
            $assets.libraries[$key] = @{ type = 'package'; sha512 = $entry.contentHash }
            Set-Content (Join-Path $PublishPath "$name.txt") 'synthetic package asset'
            $source = Join-Path $cache "$($key.ToLowerInvariant())/$name.txt"
            $null = New-Item -ItemType Directory -Path (Split-Path $source) -Force
            Copy-Item (Join-Path $PublishPath "$name.txt") $source
        }
    }
    foreach ($name in @('Desktop', 'Application', 'Domain', 'Infrastructure', 'Overlay')) {
        $key = "SoulsTracker.$name/$version"
        $deps.libraries[$key] = @{ type = 'project' }
        $deps.targets[$targetName][$key] = @{ runtime = @{ "SoulsTracker.$name.txt" = @{} } }
        Set-Content (Join-Path $PublishPath "SoulsTracker.$name.txt") 'synthetic product asset'
    }
    $deps.targets[$targetName]["SoulsTracker.Desktop/$version"].runtime['partial.txt'] = @{}
    Set-Content (Join-Path $PublishPath 'partial.txt') 'synthetic staged payload'
    Set-Content (Join-Path $PublishPath 'SoulsTracker.Desktop.runtimeconfig.json') '{}'
    $deps | ConvertTo-Json -Depth 30 | Set-Content (Join-Path $PublishPath 'SoulsTracker.Desktop.deps.json')
    $assetPath = Join-Path $Fixture 'src/SoulsTracker.Desktop/obj/project.assets.json'
    $null = New-Item -ItemType Directory -Path (Split-Path $assetPath) -Force
    $assets | ConvertTo-Json -Depth 30 | Set-Content $assetPath
    $packList = Join-Path $cache 'microsoft.windowsdesktop.app.runtime.win-x64/10.0.9/data/RuntimeList.xml'
    $null = New-Item -ItemType Directory -Path (Split-Path $packList) -Force
    Set-Content $packList '<FileList><File Type="Resources" Culture="fr" Path="runtimes/win-x64/lib/net10.0/fr/Example.resources.dll" /></FileList>'
    $null = New-Item -ItemType Directory -Path (Join-Path $PublishPath 'fr')
    Set-Content (Join-Path $PublishPath 'fr/Example.resources.dll') 'synthetic satellite asset'
    $source = Join-Path $cache 'microsoft.windowsdesktop.app.runtime.win-x64/10.0.9/runtimes/win-x64/lib/net10.0/fr/Example.resources.dll'
    $null = New-Item -ItemType Directory -Path (Split-Path $source) -Force
    Copy-Item (Join-Path $PublishPath 'fr/Example.resources.dll') $source
}

if ($DefineFixturesOnly) { return }

if ([string]::IsNullOrWhiteSpace($PayloadPath)) {
    $fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('SoulsTracker-release-content-' + [guid]::NewGuid().ToString('N'))
    try {
        foreach ($case in @('valid', 'missing-license', 'missing-notices', 'missing-text', 'changed-text', 'changed-upstream-notice', 'drift', 'lock-drift', 'runtime-drift', 'missing-runtime', 'missing-satellite', 'symbol', 'private-directory', 'database', 'save', 'pairing', 'unknown-file', 'local-path', 'upstream-path', 'changed-upstream', 'upstream-private-path', 'template-path', 'template-private-path', 'template-changed-section', 'product-private-path', 'product-debug-record')) {
            $fixture = Join-Path $fixtureRoot $case
            $payload = Join-Path $fixture 'payload'
            New-DistributionFixture $fixture $payload
            Set-Content (Join-Path $payload 'SoulsTracker.Desktop.exe') 'synthetic executable; never launched'
            $export = Join-Path $fixture 'scripts/Export-ThirdPartyNotices.ps1'
            $expected = ''
            switch ($case) {
                'missing-license' { Remove-Item (Join-Path $fixture 'LICENSE'); $expected = 'Required distribution input is missing' }
                'missing-notices' { Remove-Item (Join-Path $fixture 'docs/THIRD_PARTY_NOTICES.md'); $expected = 'Required distribution input is missing' }
                'missing-text' { (Get-Content -Raw (Join-Path $fixture 'docs/THIRD_PARTY_NOTICES.md')).Replace('<!-- BEGIN sqlitepcl-notice -->', '<!-- absent -->') | Set-Content (Join-Path $fixture 'docs/THIRD_PARTY_NOTICES.md'); $expected = 'text is missing' }
                'changed-text' { (Get-Content -Raw (Join-Path $fixture 'docs/THIRD_PARTY_NOTICES.md')).Replace('<!-- BEGIN sqlitepcl-notice -->', "<!-- BEGIN sqlitepcl-notice -->`nchanged") | Set-Content (Join-Path $fixture 'docs/THIRD_PARTY_NOTICES.md'); $expected = 'text has changed' }
                'changed-upstream-notice' { Add-Content (Join-Path $fixture 'cache/microsoft.web.webview2/1.0.4191.47/NOTICE.txt') 'changed'; $expected = 'does not match the restored upstream package' }
                'drift' { (Get-Content -Raw (Join-Path $payload 'SoulsTracker.Desktop.deps.json')).Replace('10.0.10', '99.0.0') | Set-Content (Join-Path $payload 'SoulsTracker.Desktop.deps.json'); $expected = 'inventory drift' }
                'lock-drift' { (Get-Content -Raw (Join-Path $fixture 'src/SoulsTracker.Desktop/packages.lock.json')).Replace('10.0.10', '99.0.0') | Set-Content (Join-Path $fixture 'src/SoulsTracker.Desktop/packages.lock.json'); $expected = 'inventory drift' }
                'runtime-drift' { (Get-Content -Raw (Join-Path $fixture 'src/SoulsTracker.Desktop/obj/project.assets.json')).Replace('10.0.9', '99.0.0') | Set-Content (Join-Path $fixture 'src/SoulsTracker.Desktop/obj/project.assets.json'); $expected = 'runtime pack is missing' }
                'missing-runtime' { Remove-Item (Join-Path $payload 'SourceGear.sqlite3.txt'); $expected = 'required runtime/native/resource asset is missing' }
                'missing-satellite' { Remove-Item (Join-Path $payload 'fr/Example.resources.dll'); $expected = 'required runtime/native/resource asset is missing' }
                'symbol' { Set-Content (Join-Path $payload 'synthetic.pdb') 'synthetic symbol'; $expected = 'prohibited file' }
                'private-directory' { $null = New-Item -ItemType Directory (Join-Path $payload 'agents'); $expected = 'prohibited file' }
                'database' { Set-Content (Join-Path $payload 'synthetic.db') 'synthetic database'; $expected = 'prohibited file' }
                'save' { Set-Content (Join-Path $payload 'synthetic.sl2') 'synthetic save'; $expected = 'prohibited file' }
                'pairing' { Set-Content (Join-Path $payload 'pairing.json') 'synthetic pairing, no credentials'; $expected = 'prohibited file' }
                'unknown-file' { Set-Content (Join-Path $payload 'extra.txt') 'synthetic unknown input'; $expected = 'unrecognized file' }
                'local-path' { Set-Content (Join-Path $payload 'SoulsTracker.Desktop.exe') 'C:\Users\Synthetic\source\example.pdb'; $expected = 'local source/build path' }
                { $_ -in @('upstream-path', 'changed-upstream', 'upstream-private-path') } {
                    $relative = 'fr/Example.resources.dll'
                    $source = Join-Path $fixture "cache/microsoft.windowsdesktop.app.runtime.win-x64/10.0.9/runtimes/win-x64/lib/net10.0/$relative"
                    Set-Content $source 'synthetic upstream record C:\build\upstream\example.pdb'
                    Copy-Item $source (Join-Path $payload $relative) -Force
                    if ($case -eq 'changed-upstream') { Add-Content (Join-Path $payload $relative) 'unexpected bytes'; $expected = 'differs from restored upstream' }
                    if ($case -eq 'upstream-private-path') { Add-Content (Join-Path $payload $relative) 'C:\Users\Synthetic\private\example.pdb'; $expected = 'differs from restored upstream' }
                }
                { $_ -in @('template-path', 'template-private-path', 'template-changed-section', 'product-private-path', 'product-debug-record') } {
                    $dotnetRoot = Split-Path (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source
                    $template = Join-Path $dotnetRoot 'packs/Microsoft.NETCore.App.Host.win-x64/10.0.9/runtimes/win-x64/native/apphost.exe'
                    $destination = Join-Path $payload 'SoulsTracker.Desktop.exe'
                    if ($case -like 'product-*') {
                        $destination = Join-Path $payload 'SoulsTracker.Domain.dll'
                        $depsPath = Join-Path $payload 'SoulsTracker.Desktop.deps.json'
                        (Get-Content -Raw $depsPath).Replace('SoulsTracker.Domain.txt', 'SoulsTracker.Domain.dll') | Set-Content $depsPath
                        Remove-Item (Join-Path $payload 'SoulsTracker.Domain.txt')
                    }
                    Copy-Item $template $destination
                    if ($case -eq 'template-private-path') {
                        $stream = [IO.File]::OpenWrite($destination)
                        try { $null = $stream.Seek(0, [IO.SeekOrigin]::End); $bytes = [Text.Encoding]::Unicode.GetBytes('C:\Users\Synthetic\private\example.pdb'); $stream.Write($bytes) } finally { $stream.Dispose() }
                        $expected = 'local source/build path'
                    }
                    if ($case -eq 'template-changed-section') {
                        $bytes = [IO.File]::ReadAllBytes($destination)
                        $stream = [IO.MemoryStream]::new($bytes, $false)
                        $pe = [Reflection.PortableExecutable.PEReader]::new($stream)
                        try { $offset = @($pe.ReadDebugDirectory() | Where-Object { $_.Type.ToString() -eq 'CodeView' })[0].DataPointer + 24 } finally { $pe.Dispose(); $stream.Dispose() }
                        # Keep a syntactically valid absolute path but change its drive byte.
                        $bytes[$offset] = if ($bytes[$offset] -eq 90) { 89 } else { 90 }
                        [IO.File]::WriteAllBytes($destination, $bytes)
                        $expected = 'local source/build path'
                    }
                    if ($case -eq 'product-private-path') { Set-Content $destination 'C:\Users\Synthetic\private\example.pdb'; $expected = 'local source/build path' }
                    if ($case -eq 'product-debug-record') {
                        $bytes = [IO.File]::ReadAllBytes($destination)
                        $stream = [IO.MemoryStream]::new($bytes, $false)
                        $pe = [Reflection.PortableExecutable.PEReader]::new($stream)
                        try { $entry = @($pe.ReadDebugDirectory() | Where-Object { $_.Type.ToString() -eq 'CodeView' })[0] } finally { $pe.Dispose(); $stream.Dispose() }
                        # Even a CodeView record without a path is prohibited in a product DLL.
                        [Array]::Clear($bytes, $entry.DataPointer + 24, $entry.DataSize - 24)
                        [IO.File]::WriteAllBytes($destination, $bytes)
                        $expected = 'debug-symbol record'
                    }
                }
            }
            $failure = $null
            try { & $export -PayloadPath $payload | Out-Null } catch { $failure = $_.Exception.Message }
            if ($expected) {
                Assert-Content ($null -ne $failure -and $failure.Contains($expected)) "Content case $case did not reject with the expected reason."
                Assert-Content (-not (Test-Path (Join-Path $payload 'LICENSE'))) 'Rejected content was staged with a product license.'
            } else {
                Assert-Content ($null -eq $failure) "Valid synthetic payload failed: $failure"
                & $export -PayloadPath $payload -ValidateOnly | Out-Null
                foreach ($notice in @('LICENSE', 'THIRD_PARTY_NOTICES.md')) {
                    $bytes = [IO.File]::ReadAllBytes((Join-Path $payload $notice))
                    Remove-Item (Join-Path $payload $notice)
                    $rejected = $false
                    try { & $export -PayloadPath $payload -ValidateOnly | Out-Null } catch { $rejected = $true }
                    Assert-Content $rejected 'Missing distributed notice must fail validation.'
                    [IO.File]::WriteAllBytes((Join-Path $payload $notice), $bytes)
                }
            }
            Write-Output "Content case $case passed."
        }
    } finally {
        if (Test-Path -LiteralPath $fixtureRoot) { Remove-Item -LiteralPath $fixtureRoot -Recurse -Force }
    }
    return
}

$payload = Get-Item -LiteralPath $PayloadPath
Assert-Content $payload.PSIsContainer 'The prepared Desktop payload must be a directory.'

# Check the shared payload, which is the input to both archive and installer.
# Report only fixed messages, never file contents or a local build/source path.
$licensePath = Join-Path $payload.FullName 'LICENSE'
$noticesPath = Join-Path $payload.FullName 'THIRD_PARTY_NOTICES.md'
Assert-Content (Test-Path -LiteralPath $licensePath -PathType Leaf) 'Desktop payload is missing LICENSE.'
Assert-Content (Test-Path -LiteralPath $noticesPath -PathType Leaf) 'Desktop payload is missing THIRD_PARTY_NOTICES.md.'

$licenseHash = (Get-FileHash -LiteralPath $licensePath -Algorithm SHA256).Hash
$productLicenseHash = (Get-FileHash -LiteralPath (Join-Path $root 'LICENSE') -Algorithm SHA256).Hash
Assert-Content ($licenseHash -ceq $productLicenseHash) 'Desktop payload LICENSE must be byte-identical to the product license.'

$notices = [IO.File]::ReadAllText($noticesPath)
$rootAttribution = [IO.File]::ReadAllText((Join-Path $root 'THIRD_PARTY_NOTICES.md'))
$reviewedNotices = [IO.File]::ReadAllText((Join-Path $root 'docs/THIRD_PARTY_NOTICES.md'))
Assert-Content (-not [string]::IsNullOrWhiteSpace($rootAttribution)) 'Root attribution must not be empty.'
Assert-Content (-not [string]::IsNullOrWhiteSpace($reviewedNotices)) 'Reviewed distribution notices must not be empty.'
Assert-Content ($notices.Contains($rootAttribution)) 'Desktop notices must preserve the complete root attribution.'
Assert-Content ($notices.Contains($reviewedNotices)) 'Desktop notices must preserve the complete reviewed distribution notices.'

Write-Output 'Desktop product license and notice inclusion checks passed.'

& (Join-Path $PSScriptRoot 'Export-ThirdPartyNotices.ps1') -PayloadPath $payload.FullName -ValidateOnly

# Use the same recursive portable inclusion rule as the release workflow.
$archiveRoot = Join-Path ([IO.Path]::GetTempPath()) ('SoulsTracker-content-archive-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory $archiveRoot
try {
    $archivePath = Join-Path $archiveRoot 'portable.zip'
    Compress-Archive -Path (Join-Path $payload.FullName '*') -DestinationPath $archivePath
    $expanded = Join-Path $archiveRoot 'expanded'
    Expand-Archive -LiteralPath $archivePath -DestinationPath $expanded
    $files = @(Get-ChildItem -LiteralPath $payload.FullName -Recurse -File -Force)
    $archiveFiles = @(Get-ChildItem -LiteralPath $expanded -Recurse -File -Force)
    Assert-Content ($files.Count -eq $archiveFiles.Count) 'Portable archive file count differs from shared payload.'
    foreach ($file in $files) {
        $relative = [IO.Path]::GetRelativePath($payload.FullName, $file.FullName)
        $copy = Join-Path $expanded $relative
        Assert-Content (Test-Path -LiteralPath $copy -PathType Leaf) 'Portable archive is missing a shared-payload file.'
        Assert-Content ((Get-FileHash -LiteralPath $file.FullName).Hash -ceq (Get-FileHash -LiteralPath $copy).Hash) 'Portable archive file bytes differ from shared payload.'
    }
    $installer = Get-Content -Raw (Join-Path $root 'installer/SoulsTracker.iss')
    $filesSection = [regex]::Match($installer, '(?s)\[Files\]\s*(.*?)\s*\[Icons\]').Groups[1].Value.Trim()
    $expectedFiles = @(
        'Source: "{#BuildOutput}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs',
        'Source: "{#WebView2Bootstrapper}"; Flags: dontcopy'
    ) -join [Environment]::NewLine
    Assert-Content ($filesSection.Replace("`r`n", "`n") -ceq $expectedFiles.Replace("`r`n", "`n")) 'Installer payload and WebView2 bootstrapper inclusion rules changed.'
    Write-Output "Portable archive entries/bytes and installer recursive include equivalence passed ($($files.Count) files). Installer runtime behavior was not exercised."
} finally {
    Remove-Item -LiteralPath $archiveRoot -Recurse -Force
}
