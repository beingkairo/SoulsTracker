[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'docs\THIRD_PARTY_NOTICES.md')
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$nugetRoot = Join-Path $env:USERPROFILE '.nuget\packages'
$entries = [System.Collections.Generic.List[object]]::new()

Get-ChildItem -Path $root -Recurse -Filter project.assets.json |
    ForEach-Object {
        $assets = Get-Content -Raw $_.FullName | ConvertFrom-Json
        foreach ($library in $assets.libraries.PSObject.Properties) {
            if ($library.Value.type -ne 'package') { continue }
            $name, $version = $library.Name -split '/', 2
            $key = "nuget|$name|$version"
            if ($entries.Key -contains $key) { continue }

            $nuspec = Get-ChildItem (Join-Path $nugetRoot "$($name.ToLowerInvariant())\$version") -Filter '*.nuspec' -ErrorAction SilentlyContinue | Select-Object -First 1
            $license = 'Package metadata unavailable; review NuGet package.'
            $url = "https://www.nuget.org/packages/$name/$version"
            if ($nuspec) {
                [xml]$xml = Get-Content -Raw $nuspec.FullName
                $metadata = $xml.package.metadata
                if ($metadata.license) {
                    $license = if ($metadata.license.type -eq 'expression') { [string]$metadata.license.'#text' } else { "See $($metadata.license.'#text') in package" }
                } elseif ($metadata.licenseUrl) {
                    $license = "See $($metadata.licenseUrl)"
                }
                if ($metadata.projectUrl) { $url = [string]$metadata.projectUrl }
            }
            $entries.Add([pscustomobject]@{ Key=$key; Ecosystem='NuGet'; Name=$name; Version=$version; License=$license; Source=$url })
        }
    }

$nodeModules = Join-Path $root 'web_overlay\node_modules'
if (Test-Path $nodeModules) {
    Get-ChildItem -Path $nodeModules -Recurse -Filter package.json |
        Where-Object { $_.FullName -notmatch '[\\/]\.bin[\\/]' } |
        ForEach-Object {
            $package = Get-Content -Raw $_.FullName | ConvertFrom-Json
            if ([string]::IsNullOrWhiteSpace($package.name) -or [string]::IsNullOrWhiteSpace($package.version)) { return }
            $key = "npm|$($package.name)|$($package.version)"
            if ($entries.Key -contains $key) { return }
            $license = if ($package.license) { [string]$package.license } else { 'Package metadata unavailable; review npm package.' }
            $source = if ($package.repository -is [string]) { [string]$package.repository } elseif ($package.repository.url) { [string]$package.repository.url } else { "https://www.npmjs.com/package/$($package.name)/v/$($package.version)" }
            $entries.Add([pscustomobject]@{ Key=$key; Ecosystem='npm'; Name=[string]$package.name; Version=[string]$package.version; License=$license; Source=$source })
        }
}

$lines = @(
    '# Third-party notices',
    '',
    'This inventory is generated from the locked NuGet restore graph and installed npm package metadata for the release workspace. Review the referenced package licenses and notices before distributing a binary.',
    '',
    "Generated: $([DateTime]::UtcNow.ToString('yyyy-MM-dd')) UTC",
    '',
    '| Ecosystem | Package | Version | Declared license | Source |',
    '| --- | --- | --- | --- | --- |'
)

foreach ($entry in $entries | Sort-Object Ecosystem, Name, Version) {
    $license = $entry.License -replace '\|', '\\|'
    $source = $entry.Source -replace '\|', '\\|'
    $lines += "| $($entry.Ecosystem) | $($entry.Name) | $($entry.Version) | $license | $source |"
}

New-Item -ItemType Directory -Force (Split-Path -Parent $OutputPath) | Out-Null
Set-Content -LiteralPath $OutputPath -Value $lines -Encoding utf8
Write-Output "Wrote $OutputPath with $($entries.Count) dependency entries."
