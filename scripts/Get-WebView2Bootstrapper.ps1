[CmdletBinding()]
param(
    [string]$DestinationPath,
    [switch]$DefineFunctionsOnly
)

$ErrorActionPreference = 'Stop'

function Assert-MicrosoftWebView2BootstrapperSignature {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [object]$Signature
    )

    if ($null -eq $Signature) {
        $Signature = Get-AuthenticodeSignature -LiteralPath $Path
    }

    if ($Signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or $null -eq $Signature.SignerCertificate) {
        throw 'The Microsoft WebView2 bootstrapper has an invalid Authenticode signature.'
    }

    $simpleName = $Signature.SignerCertificate.GetNameInfo(
        [System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName,
        $false)
    $subject = [string]$Signature.SignerCertificate.Subject
    if ($simpleName -cne 'Microsoft Corporation' -or $subject -notmatch '(^|,\s*)O=Microsoft Corporation(,|$)') {
        throw 'The WebView2 bootstrapper Authenticode publisher is not Microsoft Corporation.'
    }

    return $subject
}

function Save-OfficialWebView2Bootstrapper {
    param([Parameter(Mandatory)] [string]$DestinationPath)

    $officialUri = [uri]'https://go.microsoft.com/fwlink/p/?LinkId=2124703'
    $fullDestination = [IO.Path]::GetFullPath($DestinationPath)
    $destinationDirectory = Split-Path -Parent $fullDestination
    if (Test-Path -LiteralPath $fullDestination) {
        throw 'Refusing to replace an existing WebView2 bootstrapper staging file.'
    }

    $null = New-Item -ItemType Directory -Path $destinationDirectory -Force
    $downloadPath = Join-Path $destinationDirectory ((Split-Path -Leaf $fullDestination) + '.' + [guid]::NewGuid().ToString('N') + '.download')
    try {
        Invoke-WebRequest -Uri $officialUri -OutFile $downloadPath
        if (-not (Test-Path -LiteralPath $downloadPath -PathType Leaf) -or (Get-Item -LiteralPath $downloadPath).Length -le 0) {
            throw 'The Microsoft WebView2 bootstrapper download was empty or unavailable.'
        }

        $signerSubject = Assert-MicrosoftWebView2BootstrapperSignature -Path $downloadPath
        $hash = (Get-FileHash -LiteralPath $downloadPath -Algorithm SHA256).Hash
        Move-Item -LiteralPath $downloadPath -Destination $fullDestination
        return [pscustomobject]@{
            Path = $fullDestination
            Sha256 = $hash
            SignerSubject = $signerSubject
            SourceUri = $officialUri.AbsoluteUri
        }
    }
    catch {
        Remove-Item -LiteralPath $downloadPath -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $fullDestination -Force -ErrorAction SilentlyContinue
        throw
    }
}

if ($DefineFunctionsOnly) { return }
if ([string]::IsNullOrWhiteSpace($DestinationPath)) {
    throw 'DestinationPath is required.'
}

Save-OfficialWebView2Bootstrapper -DestinationPath $DestinationPath
