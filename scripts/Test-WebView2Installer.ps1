[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('SoulsTracker-webview2-installer-' + [guid]::NewGuid().ToString('N'))
$acquisitionScript = Join-Path $root 'scripts/Get-WebView2Bootstrapper.ps1'

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Assert-Rejected([scriptblock]$Action, [string]$Expected) {
    $caught = $null
    try { & $Action | Out-Null } catch { $caught = $_.Exception.Message }
    Assert-True ($null -ne $caught -and $caught.Contains($Expected)) "Expected rejection containing '$Expected'; got '$caught'."
}

function Get-SyntheticWebView2Detection([AllowNull()] [object[]]$Registrations) {
    $sawMalformed = $false
    foreach ($registration in $Registrations) {
        if ($null -eq $registration -or [string]::IsNullOrWhiteSpace([string]$registration) -or [string]$registration -ceq '0.0.0.0') {
            continue
        }

        [version]$version = [version]::new()
        if ([version]::TryParse([string]$registration, [ref]$version) -and
            $version.ToString(4) -ceq [string]$registration -and
            $version -gt [version]'0.0.0.0') {
            return 'Present'
        }

        $sawMalformed = $true
    }

    if ($sawMalformed) { return 'Malformed' }
    return 'Absent'
}

function Wait-SyntheticWebView2Registration([string[]]$Detections, [int]$MaximumAttempts) {
    $attempts = 0
    $lastDetection = 'Absent'
    while ($attempts -lt $MaximumAttempts) {
        $lastDetection = $Detections[[Math]::Min($attempts, $Detections.Count - 1)]
        $attempts++
        if ($lastDetection -ceq 'Present') {
            return [pscustomobject]@{ Detection = $lastDetection; Attempts = $attempts }
        }
    }

    return [pscustomobject]@{ Detection = $lastDetection; Attempts = $attempts }
}

try {
    . $acquisitionScript -DefineFunctionsOnly

    $script:requestedUri = $null
    $script:downloadMode = 'valid'
    function Invoke-WebRequest {
        param([uri]$Uri, [string]$OutFile)
        $script:requestedUri = $Uri.AbsoluteUri
        if ($script:downloadMode -eq 'network-failure') { throw 'Synthetic network failure.' }
        [byte[]]$bytes = if ($script:downloadMode -eq 'empty') { ,([byte[]]::new(0)) } else { ,([Text.Encoding]::UTF8.GetBytes('synthetic signed bootstrapper')) }
        [IO.File]::WriteAllBytes($OutFile, $bytes)
    }
    function Get-AuthenticodeSignature {
        param([string]$LiteralPath)
        $certificate = [pscustomobject]@{
            Subject = 'CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US'
        }
        $certificate | Add-Member -MemberType ScriptMethod -Name GetNameInfo -Value { param($Type, $ForIssuer) 'Microsoft Corporation' }
        [pscustomobject]@{ Status = [System.Management.Automation.SignatureStatus]::Valid; SignerCertificate = $certificate }
    }

    $destination = Join-Path $fixtureRoot 'MicrosoftEdgeWebview2Setup.exe'
    $result = Save-OfficialWebView2Bootstrapper -DestinationPath $destination

    Assert-True ($script:requestedUri -ceq 'https://go.microsoft.com/fwlink/p/?LinkId=2124703') 'Acquisition must use the official Microsoft Evergreen Bootstrapper link.'
    Assert-True (Test-Path -LiteralPath $destination -PathType Leaf) 'Verified bootstrapper was not recorded at the requested path.'
    Assert-True ($result.Path -ceq $destination) 'Acquisition did not return the exact recorded path.'
    Assert-True ($result.Sha256 -ceq (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash) 'Acquisition did not return the recorded file SHA-256.'
    Assert-True ($result.SignerSubject.Contains('O=Microsoft Corporation')) 'Acquisition did not record the verified publisher.'
    Assert-True (@(Get-ChildItem -LiteralPath $fixtureRoot -Filter '*.download').Count -eq 0) 'Acquisition left a temporary download behind.'

    Write-Output 'WebView2 bootstrapper acquisition success case passed.'

    $invalidSignature = [pscustomobject]@{
        Status = [System.Management.Automation.SignatureStatus]::HashMismatch
        SignerCertificate = $null
    }
    Assert-Rejected {
        Assert-MicrosoftWebView2BootstrapperSignature -Path 'synthetic.exe' -Signature $invalidSignature
    } 'invalid Authenticode signature'
    Write-Output 'WebView2 bootstrapper invalid-signature case passed.'

    $wrongCertificate = [pscustomobject]@{ Subject = 'CN=Example Publisher, O=Example Publisher, C=US' }
    $wrongCertificate | Add-Member -MemberType ScriptMethod -Name GetNameInfo -Value { param($Type, $ForIssuer) 'Example Publisher' }
    $wrongPublisher = [pscustomobject]@{
        Status = [System.Management.Automation.SignatureStatus]::Valid
        SignerCertificate = $wrongCertificate
    }
    Assert-Rejected {
        Assert-MicrosoftWebView2BootstrapperSignature -Path 'synthetic.exe' -Signature $wrongPublisher
    } 'publisher is not Microsoft Corporation'
    Write-Output 'WebView2 bootstrapper wrong-publisher case passed.'

    $script:downloadMode = 'empty'
    $emptyDestination = Join-Path $fixtureRoot 'empty/MicrosoftEdgeWebview2Setup.exe'
    Assert-Rejected {
        Save-OfficialWebView2Bootstrapper -DestinationPath $emptyDestination
    } 'empty or unavailable'
    Assert-True (-not (Test-Path -LiteralPath $emptyDestination)) 'Rejected empty download left a destination file.'
    Assert-True (@(Get-ChildItem -LiteralPath (Split-Path -Parent $emptyDestination) -Filter '*.download').Count -eq 0) 'Rejected empty download left temporary bytes.'
    Write-Output 'WebView2 bootstrapper empty-download cleanup case passed.'

    $script:downloadMode = 'network-failure'
    $failedDestination = Join-Path $fixtureRoot 'network-failure/MicrosoftEdgeWebview2Setup.exe'
    Assert-Rejected {
        Save-OfficialWebView2Bootstrapper -DestinationPath $failedDestination
    } 'Synthetic network failure'
    Assert-True (-not (Test-Path -LiteralPath $failedDestination)) 'Failed network acquisition left a destination file.'
    Assert-True (@(Get-ChildItem -LiteralPath (Split-Path -Parent $failedDestination) -Filter '*.download').Count -eq 0) 'Failed network acquisition left temporary bytes.'
    Write-Output 'WebView2 bootstrapper network-failure cleanup case passed.'

    $installer = [IO.File]::ReadAllText((Join-Path $root 'installer/SoulsTracker.iss'))
    foreach ($required in @(
        '#ifndef WebView2Bootstrapper',
        '#ifndef WebView2BootstrapperSha256',
        'Source: "{#WebView2Bootstrapper}"',
        'Flags: dontcopy',
        "'MicrosoftEdgeWebview2Setup.exe'",
        "'/silent /install'",
        'function PrepareToInstall(var NeedsRestart: Boolean): String;',
        'GetSHA256OfFile',
        'HKLM32', 'HKLM64', 'HKCU32', 'HKCU64',
        'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}',
        "'pv'",
        "(Version = '') or (Version = '0.0.0.0')",
        'Runtime installation failed',
        'Runtime is still unavailable'
    )) {
        Assert-True ($installer.Contains($required)) "Installer is missing the WebView2 prerequisite contract: $required"
    }
    Assert-True ($installer.Contains('PrivilegesRequired=lowest')) 'Installer must preserve lowest-privilege setup.'
    Assert-True (-not ($installer -match '(?m)^\[UninstallDelete\]')) 'Choice-dependent deletion cannot be registered during install.'
    Assert-True ($installer.Contains('if (CurUninstallStep = usPostUninstall) and DeleteLocalSettings then')) 'State removal must depend on the uninstall-time choice.'
    Assert-True ($installer.Contains("if (CurUninstallStep = usDone) and DeleteLocalSettings then`n    RemoveDir(ExpandConstant('{localappdata}\SoulsTracker'));")) 'DELETE must retry removal of the empty Local root at uninstall completion.'
    Assert-True ($installer.Contains("DelTree(LocalRoot + '\tracker.db', False, True, False)")) 'Installer must remove the active local tracker state on request.'
    Assert-True ($installer.Contains("DelTree(RoamingRoot + '\state.json', False, True, False)")) 'Installer must clear the legacy import source on request.'
    Assert-True (-not ($installer -match "DelTree\(RoamingRoot, True, True, True\)")) 'Uninstall must not recursively delete a mixed-content user root.'
    Assert-True ((Get-SyntheticWebView2Detection @('123.0.1.2')) -ceq 'Present') 'A valid registration must detect the Runtime.'
    Assert-True ((Get-SyntheticWebView2Detection @($null, '', '0.0.0.0')) -ceq 'Absent') 'Only missing or documented absent registrations must detect absence.'
    Assert-True ((Get-SyntheticWebView2Detection @('invalid')) -ceq 'Malformed') 'Malformed-only registration data must fail closed.'
    Assert-True ((Get-SyntheticWebView2Detection @('invalid', '123.0.1.2')) -ceq 'Present') 'Any valid registration must take precedence over stale malformed data in another view.'
    $detect = [regex]::Match($installer, '(?s)function DetectWebView2Runtime\(\): Integer;(.*?)(?=function )').Groups[1].Value
    Assert-True ($detect.IndexOf('if SawValid then') -lt $detect.IndexOf('else if SawMalformed then')) 'Installer detection must give a valid registration precedence over malformed data in another view.'
    Write-Output 'WebView2 isolated valid, absent, malformed, and valid-plus-malformed detection cases passed.'

    $immediate = Wait-SyntheticWebView2Registration @('Present') 5
    Assert-True ($immediate.Detection -ceq 'Present' -and $immediate.Attempts -eq 1) 'Post-bootstrap polling must accept immediate registration.'
    $delayed = Wait-SyntheticWebView2Registration @('Absent', 'Malformed', 'Present') 5
    Assert-True ($delayed.Detection -ceq 'Present' -and $delayed.Attempts -eq 3) 'Post-bootstrap polling must accept delayed registration, including a transient malformed read.'
    $timeout = Wait-SyntheticWebView2Registration @('Absent') 5
    Assert-True ($timeout.Detection -ceq 'Absent' -and $timeout.Attempts -eq 5) 'Post-bootstrap polling must stop at its bounded timeout.'
    $malformedTimeout = Wait-SyntheticWebView2Registration @('Malformed') 5
    Assert-True ($malformedTimeout.Detection -ceq 'Malformed' -and $malformedTimeout.Attempts -eq 5) 'Malformed-only post-bootstrap data must remain distinguishable at timeout.'

    $prepare = [regex]::Match($installer, '(?s)function PrepareToInstall\(var NeedsRestart: Boolean\): String;(.*?)(?=function InitializeUninstall)').Groups[1].Value
    $initialDetection = $prepare.IndexOf('Detection := DetectWebView2Runtime();')
    $presentSkip = $prepare.IndexOf('if Detection = WebView2Present then')
    $extract = $prepare.IndexOf("ExtractTemporaryFile('MicrosoftEdgeWebview2Setup.exe')")
    $execute = $prepare.IndexOf("Exec(BootstrapperPath, '/silent /install'")
    $postDetection = $prepare.LastIndexOf('WaitForWebView2Runtime')
    Assert-True (0 -le $initialDetection -and $initialDetection -lt $presentSkip -and $presentSkip -lt $extract) 'Present-runtime path must exit before bootstrap extraction or execution.'
    Assert-True ($extract -lt $execute -and $execute -lt $postDetection) 'Absent-runtime path must extract, execute, then re-detect.'
    Assert-True ($prepare.Contains('Detection := WaitForWebView2Runtime')) 'Successful bootstrap execution must enter bounded postcondition polling.'
    Assert-True ($installer.Contains('WebView2PollIntervalMilliseconds = 250')) 'Post-bootstrap polling must use a deliberate responsive interval.'
    Assert-True ($installer.Contains('WebView2PollMaximumAttempts = 480')) 'Post-bootstrap polling must use a deliberate two-minute bound.'
    Assert-True ($installer.Contains('ProgressPage.SetProgress')) 'Post-bootstrap polling must pump installer UI messages while waiting.'
    Assert-True ($prepare.Contains('if ExitCode <> 0 then') -and $prepare.IndexOf('if ExitCode <> 0 then') -lt $postDetection) 'A nonzero bootstrapper exit must fail before postcondition polling.'
    Assert-True ($prepare.Contains('else if Detection <> WebView2Present then')) 'Post-bootstrap timeout must reject a missing postcondition.'
    Write-Output 'WebView2 installer immediate, delayed, timeout, malformed-timeout, and nonzero-exit contracts passed.'

    $buildScript = [IO.File]::ReadAllText((Join-Path $root 'scripts/Build-Release.ps1'))
    foreach ($required in @(
        'Test-WebView2Installer.ps1',
        'Get-WebView2Bootstrapper.ps1',
        'artifacts\staging\webview2',
        '/DWebView2Bootstrapper=',
        '/DWebView2BootstrapperSha256=',
        'WebView2 bootstrapper changed after signature verification',
        'Remove-Item -LiteralPath $webView2StagingPath -Recurse -Force'
    )) {
        Assert-True ($buildScript.Contains($required)) "Release build is missing the WebView2 packaging contract: $required"
    }
    Write-Output 'WebView2 release-build integration contract passed.'

    $releaseGuide = [IO.File]::ReadAllText((Join-Path $root 'docs/RELEASE-GETTING-STARTED.md'))
    Assert-True ($releaseGuide.Contains('installer checks for Microsoft WebView2 Runtime and installs it when needed')) 'Public setup guidance must describe the installer prerequisite behavior.'
    $runtimeLicensePath = Join-Path $root 'docs/WEBVIEW2_RUNTIME_LICENSE.txt'
    Assert-True (Test-Path -LiteralPath $runtimeLicensePath -PathType Leaf) 'The official WebView2 Runtime license terms must be retained for installer acceptance.'
    $runtimeLicense = [IO.File]::ReadAllText($runtimeLicensePath).Replace("`r`n", "`n")
    Assert-True ($runtimeLicense.Contains('MICROSOFT EDGE WEBVIEW2 RUNTIME') -and $runtimeLicense.Contains('9.    REQUIRED NOTICES TO END USERS.')) 'The retained Runtime terms are incomplete.'
    $runtimeLicenseHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($runtimeLicense))).ToLowerInvariant()
    Assert-True ($runtimeLicenseHash -ceq '241ede13d0d26886ab2a998926245a76db6a68237e3229f3d0c918db13da3b5e') 'The retained Runtime terms differ from the reviewed official Microsoft source.'
    Assert-True ($installer.Contains('LicenseFile=..\docs\WEBVIEW2_RUNTIME_LICENSE.txt')) 'Installer users must accept the retained WebView2 Runtime terms.'
    $thirdPartyNotices = [IO.File]::ReadAllText((Join-Path $root 'docs/THIRD_PARTY_NOTICES.md'))
    Assert-True ($thirdPartyNotices.Contains('https://developer.microsoft.com/microsoft-edge/api/eula/webview2')) 'Runtime terms must record the exact official Microsoft source.'
    Write-Output 'WebView2 public setup guidance contract passed.'
} finally {
    Remove-Item Function:\Invoke-WebRequest -ErrorAction SilentlyContinue
    Remove-Item Function:\Get-AuthenticodeSignature -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $fixtureRoot) { Remove-Item -LiteralPath $fixtureRoot -Recurse -Force }
}
