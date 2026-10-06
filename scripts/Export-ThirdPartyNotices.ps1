[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$PayloadPath,
    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Add-Type -AssemblyName System.Reflection.Metadata

function Assert-Distribution([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Format-InventoryKey([string]$Key) {
    # deps.json is a build input, but a malformed key must not print a local path.
    if ($Key -cmatch '^[A-Za-z0-9_.+-]+/[A-Za-z0-9_.+-]+$') { return $Key }
    return '[invalid inventory key]'
}

function Assert-InventoryKeys([object[]]$Actual, [object[]]$Expected, [string]$Category) {
    $unexpected = @($Actual | Where-Object { $_ -cnotin $Expected } | Sort-Object -Unique | ForEach-Object { Format-InventoryKey $_ })
    $missing = @($Expected | Where-Object { $_ -cnotin $Actual } | Sort-Object -Unique | ForEach-Object { Format-InventoryKey $_ })
    if ($unexpected.Count -or $missing.Count) {
        throw "Unsupported distribution inventory drift ($Category): unexpected [$($unexpected -join ', ')]; missing [$($missing -join ', ')]."
    }
}

function Read-RequiredText([string]$Path) {
    Assert-Distribution (Test-Path -LiteralPath $Path -PathType Leaf) 'Required distribution input is missing.'
    $text = [IO.File]::ReadAllText($Path)
    Assert-Distribution (-not [string]::IsNullOrWhiteSpace($text)) 'Required distribution input is empty.'
    return $text
}

# The self-contained Desktop project's exact runtime pin is the distribution source of truth.
[xml]$desktopProject = Read-RequiredText (Join-Path $root 'src/SoulsTracker.Desktop/SoulsTracker.Desktop.csproj')
$runtimePins = @($desktopProject.Project.PropertyGroup.RuntimeFrameworkVersion | Where-Object { $_ })
$patchPolicies = @($desktopProject.Project.PropertyGroup.TargetLatestRuntimePatch | Where-Object { $_ })
Assert-Distribution ($runtimePins.Count -eq 1 -and $runtimePins[0] -match '^10\.0\.[0-9]+$' -and
    $patchPolicies.Count -eq 1 -and $patchPolicies[0] -ceq 'false') 'Desktop must pin one exact runtime version and disable latest-patch resolution.'
$desktopRuntimeVersion = [string]$runtimePins[0]

function Resolve-RestoredFile([string]$Relative) {
    Assert-Distribution ($Relative -notmatch '(^|/)\.\.(/|$)|^[\\/]|:') 'Invalid restored asset path.'
    foreach ($folder in $assets.packageFolders.Keys) {
        $path = Join-Path $folder $Relative
        if (Test-Path -LiteralPath $path -PathType Leaf) { return $path }
    }
    throw 'Required restored upstream asset is missing.'
}

function Test-ApphostTemplatePaths([byte[]]$Bytes, [object[]]$PathMatches) {
    # The SDK copies the native host template, then adds product resources and
    # the managed entry-point name. Only unchanged template sections can explain
    # upstream paths. A new path in resources, padding or appended bytes fails.
    $dotnet = Get-Command dotnet -CommandType Application | Select-Object -First 1
    $templatePath = Join-Path (Split-Path $dotnet.Source) "packs/Microsoft.NETCore.App.Host.win-x64/$desktopRuntimeVersion/runtimes/win-x64/native/apphost.exe"
    if (-not (Test-Path -LiteralPath $templatePath -PathType Leaf)) { return $false }
    $templateBytes = [IO.File]::ReadAllBytes($templatePath)
    $stream = [IO.MemoryStream]::new($Bytes, $false)
    $templateStream = [IO.MemoryStream]::new($templateBytes, $false)
    $pe = [Reflection.PortableExecutable.PEReader]::new($stream)
    $template = [Reflection.PortableExecutable.PEReader]::new($templateStream)
    try {
        foreach ($match in $PathMatches) {
            $sections = @($pe.PEHeaders.SectionHeaders | Where-Object { $_.Name -ne '.rsrc' -and $match.Offset -ge $_.PointerToRawData -and ($match.Offset + $match.Length) -le ($_.PointerToRawData + $_.SizeOfRawData) })
            if ($sections.Count -ne 1) { return $false }
            $section = $sections[0]
            $original = @($template.PEHeaders.SectionHeaders | Where-Object { $_.Name -ceq $section.Name -and $_.PointerToRawData -eq $section.PointerToRawData -and $_.SizeOfRawData -eq $section.SizeOfRawData })
            if ($original.Count -ne 1 -or ($section.PointerToRawData + $section.SizeOfRawData) -gt $Bytes.Length -or ($section.PointerToRawData + $section.SizeOfRawData) -gt $templateBytes.Length) { return $false }
            $hash = [Security.Cryptography.SHA256]::Create()
            try {
                $actualHash = [Convert]::ToHexString($hash.ComputeHash($Bytes, $section.PointerToRawData, $section.SizeOfRawData))
                $originalHash = [Convert]::ToHexString($hash.ComputeHash($templateBytes, $section.PointerToRawData, $section.SizeOfRawData))
                if ($actualHash -cne $originalHash) { return $false }
            } finally { $hash.Dispose() }
        }
        return $true
    } catch { return $false }
    finally { $pe.Dispose(); $template.Dispose(); $stream.Dispose(); $templateStream.Dispose() }
}

# Only this Desktop restore and its published dependency graph are distribution inputs.
$payload = (Get-Item -LiteralPath $PayloadPath).FullName
Assert-Distribution (Test-Path -LiteralPath (Join-Path $payload 'SoulsTracker.Desktop.exe') -PathType Leaf) 'The staged desktop publish is incomplete: SoulsTracker.Desktop.exe is missing.'
$null = Read-RequiredText (Join-Path $payload 'SoulsTracker.Desktop.runtimeconfig.json')
$deps = (Read-RequiredText (Join-Path $payload 'SoulsTracker.Desktop.deps.json')) | ConvertFrom-Json -AsHashtable
$assets = (Read-RequiredText (Join-Path $root 'src/SoulsTracker.Desktop/obj/project.assets.json')) | ConvertFrom-Json -AsHashtable
$lock = (Read-RequiredText (Join-Path $root 'src/SoulsTracker.Desktop/packages.lock.json')) | ConvertFrom-Json -AsHashtable
$reviewed = Read-RequiredText (Join-Path $root 'docs/THIRD_PARTY_NOTICES.md')
$attribution = Read-RequiredText (Join-Path $root 'THIRD_PARTY_NOTICES.md')
$null = Read-RequiredText (Join-Path $root 'LICENSE')
$expected = @{
    'Microsoft.NETCore.App.Runtime.win-x64' = $desktopRuntimeVersion
    'Microsoft.WindowsDesktop.App.Runtime.win-x64' = $desktopRuntimeVersion
    'Microsoft.Web.WebView2' = '1.0.4191.47'
    'Microsoft.Data.Sqlite.Core' = '10.0.10'
    'SourceGear.sqlite3' = '3.50.4.5'
    'SQLitePCLRaw.bundle_e_sqlite3' = '3.0.3'
    'SQLitePCLRaw.config.e_sqlite3' = '3.0.3'
    'SQLitePCLRaw.core' = '3.0.3'
    'SQLitePCLRaw.provider.e_sqlite3' = '3.0.3'
}
$products = @('SoulsTracker.Desktop', 'SoulsTracker.Application', 'SoulsTracker.Domain', 'SoulsTracker.Infrastructure', 'SoulsTracker.Overlay')
$webView2References = @{
    'Microsoft.Web.WebView2.Core' = 'lib_manual/netcoreapp3.0/Microsoft.Web.WebView2.Core.dll'
    'Microsoft.Web.WebView2.WinForms' = 'lib_manual/netcoreapp3.0/Microsoft.Web.WebView2.WinForms.dll'
    'Microsoft.Web.WebView2.Wpf' = 'lib_manual/net5.0-windows10.0.17763.0/Microsoft.Web.WebView2.Wpf.dll'
}
$version = & (Join-Path $root 'eng/Get-Version.ps1')
$seen = @{}
$projectCount = 0
Assert-Distribution ($deps.runtimeTarget.name -ceq '.NETCoreApp,Version=v10.0/win-x64') 'Unsupported Desktop runtime target.'
$target = $deps.targets[$deps.runtimeTarget.name]
Assert-InventoryKeys @($target.Keys) @($deps.libraries.Keys) 'target/libraries'
foreach ($key in $deps.libraries.Keys) {
    $name, $resolved = $key -split '/', 2
    $type = $deps.libraries[$key].type
    if ($type -eq 'project') {
        $expectedProject = if ($name -cin $products) { @("$name/$version") } else { @() }
        $category = if ($expectedProject.Count -gt 0) { 'product version mismatch' } else { 'products' }
        Assert-InventoryKeys @($key) $expectedProject $category
        $projectCount++
        continue
    }
    if ($type -eq 'reference') {
        $expectedReference = if ($webView2References.ContainsKey($name)) { @("$name/$($expected['Microsoft.Web.WebView2'])") } else { @() }
        $category = if ($expectedReference.Count -gt 0) { 'WebView2 reference version mismatch' } else { 'WebView2 references' }
        Assert-InventoryKeys @($key) $expectedReference $category
        continue
    }
    if ($type -eq 'runtimepack') { $name = $name -creplace '^runtimepack\.', '' }
    if ($type -notin @('package', 'runtimepack') -or -not $expected.ContainsKey($name)) {
        throw "Unsupported distribution inventory drift (unexpected dependency): unexpected [$(Format-InventoryKey $key)]; missing []."
    }
    if ($resolved -cne $expected[$name]) {
        throw "Unsupported distribution inventory drift (version mismatch): $(Format-InventoryKey $key); expected $(Format-InventoryKey "$name/$($expected[$name])")."
    }
    if ($seen.ContainsKey($name)) {
        throw "Unsupported distribution inventory drift (duplicate dependency): $(Format-InventoryKey $key)."
    }
    $seen[$name] = $resolved
    Assert-Distribution ($reviewed.Contains("| $name | $resolved |")) 'Reviewed distribution inventory is missing or stale.'
    if ($type -eq 'package') {
        $restored = $assets.libraries[$key]
        $locked = @($lock.dependencies.Values | ForEach-Object { if ($_.Contains($name)) { $_[$name] } })
        Assert-Distribution ($null -ne $restored -and $locked.Count -gt 0) 'Distribution package is missing from restored or locked inputs.'
        foreach ($entry in $locked) {
            Assert-Distribution ($entry.resolved -ceq $resolved -and $entry.contentHash -ceq $restored.sha512 -and $deps.libraries[$key].sha512 -ceq "sha512-$($entry.contentHash)") 'Distribution restore/lock inventory drift.'
        }
    } else {
        $downloads = @($assets.project.frameworks.Values | ForEach-Object { $_.downloadDependencies } | Where-Object { $_.name -ceq $name -and $_.version -ceq "[$resolved, $resolved]" })
        Assert-Distribution ($downloads.Count -gt 0) 'Distribution runtime pack differs from the Desktop project pin or is missing from restored inputs.'
    }
}
Assert-InventoryKeys @($seen.Keys | ForEach-Object { "$_/$($seen[$_])" }) @($expected.Keys | ForEach-Object { "$_/$($expected[$_])" }) 'dependencies'
if ($projectCount -ne $products.Count) {
    $actualProjects = @($deps.libraries.Keys | Where-Object { $deps.libraries[$_].type -eq 'project' })
    $expectedProjects = @($products | ForEach-Object { "$_/$version" })
    Assert-InventoryKeys $actualProjects $expectedProjects 'projects'
}
$inventoryRows = [regex]::Matches($reviewed, '(?m)^\| ([^|]+) \| ([0-9][^|]*) \|')
Assert-Distribution ($inventoryRows.Count -eq $expected.Count) 'Reviewed distribution inventory contains unsupported entries.'

# Pin complete reviewed legal texts after the documented LF/trailing-space normalization.
$textHashes = @{
    'netcore-license' = 'cfc21f5e8bd655ae997eec916138b707b1d290b83272c02a95c9f821b8c87310'
    'netcore-notices' = '66f1d4e44973185519bb4aa8a9718eb22fc7af2cc532e3ae9cfc4c127ee7fc54'
    'desktop-license' = 'ae48df11a335dc1a615f4f938b69cba73bcf4485c4f97af49b38efb0f216353b'
    'wpf-notices' = '55fcac28c047e0d453d91d091501929349566262901566d222590b6986386f57'
    'winforms-notices' = '252050abc9391903f425f3eb542ed761d72300390565b75d21864ec2b4203b99'
    'sqlite-managed-license' = 'ae48df11a335dc1a615f4f938b69cba73bcf4485c4f97af49b38efb0f216353b'
    'sqlite-native-license' = '99464c3a88df7b708ce59e462cdcb85f72dfc9b1335b4fcc68be56131b634b95'
    'sqlitepcl-license' = 'cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30'
    'sqlitepcl-notice' = 'b038376ce12e87dc738874110969591b90620eaac5c73ffa4abef991da48188e'
    'webview2-license' = '2b39e78c5ea2ac66e1351236372b7d676ceca22b432fd9275f10b77f64abc3ef'
    'webview2-notice' = 'ee9973a1c8ac4f0a7946197ebc458ed5fe3218f8b16707994599ca33be770de7'
}
$normalized = $reviewed.Replace("`r`n", "`n")
foreach ($id in $textHashes.Keys) {
    $sections = [regex]::Matches($normalized, "(?s)<!-- BEGIN $id -->`n(.*?)`n<!-- END $id -->")
    Assert-Distribution ($sections.Count -eq 1) 'Required upstream license/notice text is missing or duplicated.'
    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($sections[0].Groups[1].Value)))
    Assert-Distribution ($hash -eq $textHashes[$id]) 'Required upstream license/notice text has changed.'
}
$restoredWebView2Notice = (Read-RequiredText (Resolve-RestoredFile 'microsoft.web.webview2/1.0.4191.47/NOTICE.txt')).Replace("`r`n", "`n")
$restoredWebView2Notice = (($restoredWebView2Notice -split "`n") | ForEach-Object { $_.TrimEnd() }) -join "`n"
$restoredWebView2Notice = $restoredWebView2Notice.TrimEnd("`n")
$embeddedWebView2Notice = [regex]::Match($normalized, '(?s)<!-- BEGIN webview2-notice -->\n(.*?)\n<!-- END webview2-notice -->').Groups[1].Value
Assert-Distribution ($embeddedWebView2Notice -ceq $restoredWebView2Notice) 'WebView2 notice does not match the restored upstream package after documented normalization.'

# Map every published runtime/native/resource asset. Keep diagnostics and satellite assemblies.
$allowed = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$upstream = @{}
foreach ($key in $target.Keys) {
    $library = $target[$key]
    foreach ($group in @('runtime', 'native', 'resources')) {
        if (-not $library.Contains($group)) { continue }
        foreach ($asset in $library[$group].Keys) {
            $leaf = ($asset -split '/')[-1]
            if ($leaf -eq '_._') { continue } # NuGet's non-runtime placeholder.
            $relative = if ($group -eq 'resources') { "$($library[$group][$asset].locale)/$leaf" } else { $leaf }
            $null = $allowed.Add($relative)
            if ($deps.libraries[$key].type -eq 'package') {
                $upstream[$relative] = "$($key.ToLowerInvariant())/$asset"
            } elseif ($deps.libraries[$key].type -eq 'runtimepack') {
                $pack = ($key -creplace '^runtimepack\.', '').ToLowerInvariant()
                $directory = if ($group -eq 'native') { 'native' } else { 'lib/net10.0' }
                $upstream[$relative] = "$pack/runtimes/win-x64/$directory/$asset"
            } elseif ($deps.libraries[$key].type -eq 'reference') {
                $name = ($key -split '/', 2)[0]
                Assert-Distribution ($webView2References.ContainsKey($name)) 'Unsupported reference asset inventory drift.'
                $upstream[$relative] = "microsoft.web.webview2/$($expected['Microsoft.Web.WebView2'])/$($webView2References[$name])"
            }
        }
    }
}
$webView2Version = $expected['Microsoft.Web.WebView2']
if (@($deps.libraries.Keys | Where-Object { $deps.libraries[$_].type -eq 'reference' }).Count -gt 0) {
    $webView2AdditionalAssets = @{
        'Microsoft.Web.WebView2.Core.xml' = "microsoft.web.webview2/$webView2Version/lib_manual/netcoreapp3.0/Microsoft.Web.WebView2.Core.xml"
        'Microsoft.Web.WebView2.WinForms.xml' = "microsoft.web.webview2/$webView2Version/lib_manual/netcoreapp3.0/Microsoft.Web.WebView2.WinForms.xml"
        'Microsoft.Web.WebView2.Wpf.xml' = "microsoft.web.webview2/$webView2Version/lib_manual/net5.0-windows10.0.17763.0/Microsoft.Web.WebView2.Wpf.xml"
        'runtimes/win-x64/native/WebView2Loader.dll' = "microsoft.web.webview2/$webView2Version/runtimes/win-x64/native/WebView2Loader.dll"
    }
    foreach ($relative in $webView2AdditionalAssets.Keys) {
        $null = $allowed.Add($relative)
        $upstream[$relative] = $webView2AdditionalAssets[$relative]
    }
}
# Windows Desktop satellite files are described by the restored pack, not Desktop deps.json.
$desktopPack = "microsoft.windowsdesktop.app.runtime.win-x64/$desktopRuntimeVersion/data/RuntimeList.xml"
$lists = @($assets.packageFolders.Keys | ForEach-Object { Join-Path $_ $desktopPack } | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
Assert-Distribution ($lists.Count -gt 0) 'Restored Windows Desktop runtime inventory is missing.'
[xml]$runtimeList = Read-RequiredText $lists[0]
foreach ($file in $runtimeList.FileList.File) {
    if ($file.Type -eq 'Resources') {
        $relative = "$($file.Culture)/$(($file.Path -split '/')[-1])"
        $null = $allowed.Add($relative)
        $upstream[$relative] = "microsoft.windowsdesktop.app.runtime.win-x64/$desktopRuntimeVersion/$($file.Path)"
    }
}
foreach ($relative in $allowed) {
    Assert-Distribution ($relative -notmatch '(^|/)\.\.(/|$)|^[\\/]|:') 'Invalid distribution asset path.'
    Assert-Distribution (Test-Path -LiteralPath (Join-Path $payload $relative) -PathType Leaf) 'A required runtime/native/resource asset is missing.'
}
foreach ($relative in @('SoulsTracker.Desktop.exe', 'SoulsTracker.Desktop.deps.json', 'SoulsTracker.Desktop.runtimeconfig.json', 'LICENSE', 'THIRD_PARTY_NOTICES.md')) {
    $null = $allowed.Add($relative)
}
foreach ($file in Get-ChildItem -LiteralPath $payload -Recurse -Force) {
    Assert-Distribution (-not ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)) 'Distribution contains a prohibited link.'
    $relative = [IO.Path]::GetRelativePath($payload, $file.FullName).Replace('\', '/')
    Assert-Distribution ($relative -notmatch '(^|/)(agents|fixtures|node_modules|\.git)(/|$)|\.(pdb|dbg|mdb|db|sqlite|sqlite3|sl2|sav|dmp|key|pem|pfx)$|(^|/)(\.env|pairing)(\.|/|$)') 'Distribution contains a prohibited file or directory.'
    if ($file.PSIsContainer) { continue }
    Assert-Distribution ($allowed.Contains($relative)) 'Distribution contains an unrecognized file.'
    if ($upstream.ContainsKey($relative) -and $file.Name -cnotin @($products | ForEach-Object { "$_.dll" })) {
        $source = Resolve-RestoredFile $upstream[$relative]
        Assert-Distribution ((Get-FileHash -LiteralPath $file.FullName).Hash -ceq (Get-FileHash -LiteralPath $source).Hash) 'Distribution asset differs from restored upstream bytes.'
        # Full-file equality, not a filename or path exemption, establishes provenance.
        continue
    }
    if ($file.Extension -in @('.dll', '.exe')) {
        # Never print matching bytes: a match may contain a local user/build path.
        $bytes = [IO.File]::ReadAllBytes($file.FullName)
        $ascii = [Text.Encoding]::ASCII.GetString($bytes)
        $localPath = '(?i)[a-z]:[\\/](users|documents and settings)[\\/]|/(home|Users)/|[a-z]:[\\/](a|agent|build|work)[\\/]'
        $pathMatches = @(
            foreach ($match in [regex]::Matches($ascii, $localPath)) { @{ Offset = $match.Index; Length = $match.Length } }
            # Scan both alignments; UTF-16 data can start at an odd binary offset.
            foreach ($alignment in @(0, 1)) {
                $unicode = [Text.Encoding]::Unicode.GetString($bytes, $alignment, $bytes.Length - $alignment)
                foreach ($match in [regex]::Matches($unicode, $localPath)) { @{ Offset = $alignment + 2 * $match.Index; Length = 2 * $match.Length } }
            }
        )
        if ($pathMatches.Count -gt 0) {
            $verifiedTemplate = $file.Name -ceq 'SoulsTracker.Desktop.exe' -and (Test-ApphostTemplatePaths $bytes $pathMatches)
            Assert-Distribution $verifiedTemplate 'Distribution contains a local source/build path.'
        }
        if ($file.Name -cin @($products | ForEach-Object { "$_.dll" })) {
            $stream = [IO.MemoryStream]::new($bytes, $false)
            $pe = [Reflection.PortableExecutable.PEReader]::new($stream)
            try {
                foreach ($entry in $pe.ReadDebugDirectory()) {
                    Assert-Distribution ($entry.Type.ToString() -notin @('CodeView', 'EmbeddedPortablePdb')) 'Product binary contains a debug-symbol record.'
                }
            } finally { $pe.Dispose(); $stream.Dispose() }
        }
    }
}

$output = $reviewed + "`n`n" + $attribution
if ($ValidateOnly) {
    Assert-Distribution ((Read-RequiredText (Join-Path $payload 'THIRD_PARTY_NOTICES.md')) -ceq $output) 'Distributed notices do not match the reviewed texts and attribution.'
    Assert-Distribution ((Get-FileHash -LiteralPath (Join-Path $payload 'LICENSE')).Hash -ceq (Get-FileHash -LiteralPath (Join-Path $root 'LICENSE')).Hash) 'Distributed product LICENSE has changed.'
} else {
    Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Join-Path $payload 'LICENSE')
    [IO.File]::WriteAllText((Join-Path $payload 'THIRD_PARTY_NOTICES.md'), $output, [Text.UTF8Encoding]::new($false))
}
Write-Output 'Desktop distribution inventory, legal texts and content checks passed.'
