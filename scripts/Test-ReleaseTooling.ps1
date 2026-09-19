[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$version = & (Join-Path $root "eng/Get-Version.ps1")
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ("SoulsTracker-release-tooling-" + [guid]::NewGuid().ToString("N"))
$null = New-Item -ItemType Directory -Path $fixtureRoot
. (Join-Path $root 'scripts/Test-ReleaseContent.ps1') -DefineFixturesOnly

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Assert-Rejected([scriptblock]$Action, [string]$Expected) {
    $caught = $null
    try { & $Action | Out-Null } catch { $caught = $_.Exception.Message }
    Assert-True ($null -ne $caught -and $caught.Contains($Expected)) "Expected rejection containing '$Expected'; got '$caught'."
}

function New-PackageFixture([string]$Name) {
    $fixture = Join-Path $fixtureRoot $Name
    foreach ($relative in @(
        "scripts/Build-Release.ps1", "scripts/Verify-Version.ps1", "scripts/Verify-ReleaseGuide.ps1",
        "eng/Get-Version.ps1", "eng/Version.props", "README.md", "docs/RELEASE-GETTING-STARTED.md",
        "docs/releases/v$version.md", ".github/workflows/release.yml",
        "web_overlay/package.json", "web_overlay/package-lock.json"
    )) {
        $destination = Join-Path $fixture $relative
        $null = New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force
        Copy-Item -LiteralPath (Join-Path $root $relative) -Destination $destination
    }
    return $fixture
}

# These commands are visible only within this test process. No real build or npm command runs.
function Invoke-FakeCommand([string]$Tool, [object[]]$Arguments) {
    $command = "$Tool $($Arguments -join ' ')"
    # Resolve the caller's case state through the scope chain, including called scripts.
    $packageCaseState.Calls.Add($command)
    $global:LASTEXITCODE = 0
    if ($Tool -eq 'dotnet' -and $Arguments[0] -eq 'publish') {
        $packageCaseState.Publishes++
        Assert-True ($Arguments[1] -eq (Join-Path $packageCaseState.Fixture 'src\SoulsTracker.Desktop\SoulsTracker.Desktop.csproj')) 'Publish must name Desktop explicitly.'
        Assert-True (($Arguments[2..5] -join ' ') -eq '--configuration Release --no-restore --output') 'Publish configuration changed.'
        $output = $Arguments[6]
        Assert-True ($output -eq (Join-Path $packageCaseState.Fixture 'artifacts\staging\desktop')) 'Publish must use staging.'
        foreach ($option in @('-p:DebugType=None', '-p:DebugSymbols=false', '-p:CopyOutputSymbolsToPublishDirectory=false')) {
            Assert-True ($Arguments -ccontains $option) 'Publish must suppress end-user debug symbols without changing developer builds.'
        }
        New-DistributionFixture $packageCaseState.Fixture $output
        Set-Content (Join-Path $output 'partial.txt') 'synthetic staged payload'
        if ($packageCaseState.Failure -ne 'missing-executable') {
            Set-Content (Join-Path $output 'SoulsTracker.Desktop.exe') 'synthetic executable; never launched'
        }
        switch ($packageCaseState.Failure) {
            'content-notices' { Remove-Item (Join-Path $packageCaseState.Fixture 'docs/THIRD_PARTY_NOTICES.md') }
            'content-drift' { (Get-Content -Raw (Join-Path $output 'SoulsTracker.Desktop.deps.json')).Replace('10.0.10', '99.0.0') | Set-Content (Join-Path $output 'SoulsTracker.Desktop.deps.json') }
            'content-symbol' { Set-Content (Join-Path $output 'synthetic.pdb') 'synthetic symbol' }
            'content-upstream' { Add-Content (Join-Path $output 'fr/Example.resources.dll') 'C:\Users\Synthetic\private\example.pdb' }
            'content-path' { Set-Content (Join-Path $output 'SoulsTracker.Desktop.exe') 'C:\Users\Synthetic\private\example.pdb' }
        }
    }
    # Missing-executable changes only the payload; its fixture path also contains that name.
    if ($packageCaseState.Failure -and $packageCaseState.Failure -ne 'missing-executable' -and $packageCaseState.Failure -notlike 'content-*' -and $command.Contains($packageCaseState.Failure)) { $global:LASTEXITCODE = 17 }
}
function dotnet { Invoke-FakeCommand 'dotnet' $args }
function npm { Invoke-FakeCommand 'npm' $args }

function Invoke-PackageCase([string]$Name, [string]$Failure = '', [string]$InvalidInput = '', [bool]$Previous = $true) {
    $packageCaseState = @{
        Fixture = New-PackageFixture $Name
        Failure = $Failure
        Calls = [Collections.Generic.List[string]]::new()
        Publishes = 0
    }
    $release = Join-Path $packageCaseState.Fixture 'artifacts/desktop'
    $staging = Join-Path $packageCaseState.Fixture 'artifacts/staging/desktop'
    if ($Previous) {
        $null = New-Item -ItemType Directory -Path $release -Force
        Set-Content (Join-Path $release 'previous.txt') 'previous release'
    }
    if ($InvalidInput -eq 'version') {
        Set-Content (Join-Path $packageCaseState.Fixture 'web_overlay/package.json') '{"version":"1.0.0"}'
    } elseif ($InvalidInput -eq 'guide') {
        Set-Content (Join-Path $packageCaseState.Fixture 'docs/RELEASE-GETTING-STARTED.md') 'obsolete guide'
    }
    $errorMessage = $null
    try { & (Join-Path $packageCaseState.Fixture 'scripts/Build-Release.ps1') -SkipInstaller | Out-Null }
    catch { $errorMessage = "$($_.Exception.Message)`n$($_.ScriptStackTrace)" }
    if ($Failure -or $InvalidInput) {
        $expected = if ($InvalidInput -eq 'version') { 'internal private package' }
        elseif ($InvalidInput -eq 'guide') { 'either startup order is supported' }
        elseif ($Failure -eq 'missing-executable') { 'SoulsTracker.Desktop.exe is missing' }
        elseif ($Failure -eq 'content-notices') { 'Required distribution input is missing' }
        elseif ($Failure -eq 'content-drift') { 'inventory drift' }
        elseif ($Failure -eq 'content-symbol') { 'prohibited file' }
        elseif ($Failure -eq 'content-upstream') { 'differs from restored upstream' }
        elseif ($Failure -eq 'content-path') { 'local source/build path' }
        else { 'failed with exit code 17' }
        Assert-True ($null -ne $errorMessage -and $errorMessage.Contains($expected)) "$Name must fail for $expected; got $errorMessage"
        if ($Failure -eq 'missing-executable') {
            Assert-True ($packageCaseState.Publishes -eq 1) 'Missing-executable must reach publish before promotion rejects the payload.'
        }
        Assert-True ((Get-Content (Join-Path $release 'previous.txt')) -eq 'previous release') "$Name changed previous output."
        Assert-True (-not (Test-Path (Join-Path $release 'partial.txt'))) "$Name promoted partial output."
        Assert-True (@(Get-ChildItem (Split-Path $release) -Filter 'desktop.previous-*').Count -eq 0) "$Name moved previous output."
        if ($InvalidInput) {
            Assert-True ($packageCaseState.Calls.Count -eq 0 -and -not (Test-Path $staging)) 'Verifiers must reject before any preparation.'
        }
    } else {
        Assert-True ($null -eq $errorMessage) "$Name failed: $errorMessage"
        Assert-True ($packageCaseState.Publishes -eq 1) 'Exactly one publish is required.'
        Assert-True (Test-Path (Join-Path $release 'SoulsTracker.Desktop.exe')) 'Cold staging was not promoted.'
        Assert-True ((Get-FileHash (Join-Path $release 'LICENSE')).Hash -ceq (Get-FileHash (Join-Path $root 'LICENSE')).Hash) 'Promoted product license changed.'
        Assert-True ((Get-Content -Raw (Join-Path $release 'THIRD_PARTY_NOTICES.md')).Contains([IO.File]::ReadAllText((Join-Path $root 'THIRD_PARTY_NOTICES.md')))) 'Promoted notice is missing root attribution.'
        Assert-True (-not (Test-Path $staging)) 'Promotion must move staging.'
        if ($Previous) {
            $backups = @(Get-ChildItem (Split-Path $release) -Filter 'desktop.previous-*')
            Assert-True ($backups.Count -eq 1) 'Replacement must preserve one backup.'
            Assert-True ((Get-Content (Join-Path $backups[0].FullName 'previous.txt')) -eq 'previous release') 'Backup content changed.'
        }
        $commands = $packageCaseState.Calls -join "`n"
        foreach ($required in @('dotnet restore ', 'dotnet format ', 'dotnet build ', 'dotnet test ', 'npm exec ')) {
            Assert-True ($commands.Contains($required)) "Missing qualification: $required"
        }
        foreach ($workspace in @('web_overlay', 'cloud_overlay')) {
            foreach ($operation in @('ci', 'run check', 'run build', 'test')) {
                Assert-True ($commands.Contains("npm $operation --prefix $(Join-Path $packageCaseState.Fixture $workspace)")) "Missing $workspace $operation."
            }
        }
    }
    Write-Output "$Name passed (synthetic commands)."
}

function Invoke-Git([string[]]$Arguments) {
    $result = & git -C $script:repo @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Fixture git failed: $($result -join ' ')" }
    return $result
}

try {
    Invoke-PackageCase 'cold' -Previous $false
    Invoke-PackageCase 'replace'
    foreach ($failure in @('content-notices', 'content-drift', 'content-symbol', 'content-upstream', 'content-path')) {
        Invoke-PackageCase $failure -Failure $failure
    }
    Invoke-PackageCase 'invalid-version' -InvalidInput 'version'
    Invoke-PackageCase 'invalid-guide' -InvalidInput 'guide'
    foreach ($failure in @('dotnet restore', 'npm ci', 'npm exec', 'npm run build', 'npm run check', 'npm test', 'dotnet format', 'dotnet build', 'dotnet test', 'dotnet publish', 'missing-executable')) {
        Invoke-PackageCase ($failure.Replace(' ', '-')) -Failure $failure
    }
    foreach ($operation in @('ci', 'run check', 'run build', 'test')) {
        # Match only the cloud command, so browser success cannot mask a missing cloud gate.
        $name = 'cloud-' + $operation.Replace(' ', '-')
        $cloudPath = Join-Path (Join-Path $fixtureRoot $name) 'cloud_overlay'
        Invoke-PackageCase $name -Failure "npm $operation --prefix $cloudPath"
    }

    # Load only the unchanged helper definitions from the actual script AST.
    $tokens = $null; $parseErrors = $null
    $buildAst = [Management.Automation.Language.Parser]::ParseFile((Join-Path $root 'scripts/Build-Release.ps1'), [ref]$tokens, [ref]$parseErrors)
    Assert-True ($parseErrors.Count -eq 0) 'Build script must parse.'
    foreach ($name in @('Initialize-CleanStagingDirectory', 'Promote-VerifiedDesktopArtifact')) {
        $definition = $buildAst.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
        Assert-True ($null -ne $definition) "Missing safeguard $name."
        . ([scriptblock]::Create($definition.Extent.Text))
    }
    $safeRoot = Join-Path $fixtureRoot 'containment'
    $null = New-Item -ItemType Directory $safeRoot
    Assert-Rejected { Initialize-CleanStagingDirectory -Path $safeRoot -AllowedRoot $safeRoot } 'outside'
    Assert-Rejected { Initialize-CleanStagingDirectory -Path "$safeRoot-sibling" -AllowedRoot $safeRoot } 'outside'
    Assert-Rejected { Promote-VerifiedDesktopArtifact -StagingPath "$safeRoot-sibling" -ReleasePath (Join-Path $safeRoot 'desktop') -ArtifactsRoot $safeRoot } 'outside'
    Assert-Rejected { Promote-VerifiedDesktopArtifact -StagingPath (Join-Path $safeRoot 'stage') -ReleasePath "$safeRoot-sibling" -ArtifactsRoot $safeRoot } 'outside'

    $stage = Join-Path $safeRoot 'stage'
    $release = Join-Path $safeRoot 'desktop'
    Initialize-CleanStagingDirectory -Path $stage -AllowedRoot $safeRoot
    Set-Content (Join-Path $stage 'stale.txt') 'stale synthetic staging'
    Initialize-CleanStagingDirectory -Path $stage -AllowedRoot $safeRoot
    Assert-True (-not (Test-Path (Join-Path $stage 'stale.txt'))) 'Staging reset left stale output.'
    $null = New-Item -ItemType Directory $release
    Set-Content (Join-Path $release 'previous.txt') 'previous release'
    Set-Content (Join-Path $stage 'SoulsTracker.Desktop.exe') 'synthetic executable'
    # Fail only the stage-to-release move; let backup and rollback use the real cmdlet.
    function Move-Item {
        [CmdletBinding()]
        param([string]$LiteralPath, [string]$Destination)
        if ($LiteralPath -eq $stage) { throw 'Synthetic promotion failure.' }
        Microsoft.PowerShell.Management\Move-Item @PSBoundParameters
    }
    try {
        Assert-Rejected { Promote-VerifiedDesktopArtifact -StagingPath $stage -ReleasePath $release -ArtifactsRoot $safeRoot } 'Synthetic promotion failure'
        Assert-True ((Get-Content (Join-Path $release 'previous.txt')) -eq 'previous release') 'Failed promotion must restore the previous release.'
        Assert-True (-not (Test-Path (Join-Path $release 'SoulsTracker.Desktop.exe'))) 'Failed promotion installed partial output.'
    } finally {
        Remove-Item Function:\Move-Item
    }

    $script:repo = Join-Path $fixtureRoot 'tag-repository'
    $null = New-Item -ItemType Directory -Path (Join-Path $script:repo 'scripts') -Force
    $null = New-Item -ItemType Directory -Path (Join-Path $script:repo 'eng')
    foreach ($file in @('Get-Version.ps1', 'Get-ReleaseMetadata.ps1', 'Version.props')) {
        Copy-Item (Join-Path $root "eng/$file") (Join-Path $script:repo "eng/$file")
    }
    Copy-Item (Join-Path $root 'scripts/Verify-ReleaseTarget.ps1') (Join-Path $script:repo 'scripts/Verify-ReleaseTarget.ps1')
    $null = Invoke-Git @('init', '--quiet')
    $null = Invoke-Git @('config', 'user.name', 'Release Test')
    $null = Invoke-Git @('config', 'user.email', 'release-test@example.invalid')
    $null = Invoke-Git @('-c', 'commit.gpgsign=false', 'commit', '--allow-empty', '-m', 'First fixture commit')
    $first = [string](Invoke-Git @('rev-parse', 'HEAD'))
    $tag = "v$version"
    $null = Invoke-Git @('tag', $tag)
    $guard = Join-Path $script:repo 'scripts/Verify-ReleaseTarget.ps1'
    $null = [Management.Automation.Language.Parser]::ParseFile($guard, [ref]$tokens, [ref]$parseErrors)
    Assert-True ($parseErrors.Count -eq 0) 'Release target script must parse.'
    foreach ($event in @('push', 'workflow_dispatch')) {
        $result = & $guard -EventName $event -EventRef "refs/tags/$tag" -ExpectedCommit $first
        Assert-True ($result -ceq $tag) 'Guard must return the validated tag only.'
    }
    Assert-Rejected { & $guard -EventName push -EventRef 'refs/tags/v0.0.1' -ExpectedCommit $first } 'ref'
    Assert-Rejected { & $guard -EventName workflow_dispatch -EventRef 'refs/heads/main' -ExpectedCommit $first } 'ref'
    Assert-Rejected { & $guard -EventName pull_request -EventRef "refs/tags/$tag" -ExpectedCommit $first } 'event'
    Assert-Rejected { & $guard -EventName push -EventRef "refs/tags/$tag" -ExpectedCommit 'HEAD' } 'SHA'
    Assert-Rejected { & $guard -EventName push -EventRef "refs/tags/$tag" -ExpectedCommit ('0' * 40) } 'resolve'
    $null = Invoke-Git @('tag', '-d', $tag)
    Assert-Rejected { & $guard -EventName push -EventRef "refs/tags/$tag" -ExpectedCommit $first } 'resolve'
    $null = Invoke-Git @('-c', 'tag.gpgsign=false', 'tag', '-a', $tag, '-m', 'Annotated fixture tag')
    foreach ($event in @('push', 'workflow_dispatch')) {
        Assert-True ((& $guard -EventName $event -EventRef "refs/tags/$tag" -ExpectedCommit $first) -ceq $tag) 'Annotated tags must work.'
    }
    $tree = [string](Invoke-Git @('rev-parse', 'HEAD^{tree}'))
    Assert-Rejected { & $guard -EventName push -EventRef "refs/tags/$tag" -ExpectedCommit $tree } 'resolve'
    $null = Invoke-Git @('-c', 'commit.gpgsign=false', 'commit', '--allow-empty', '-m', 'Second fixture commit')
    $second = [string](Invoke-Git @('rev-parse', 'HEAD'))
    Assert-Rejected { & $guard -EventName push -EventRef "refs/tags/$tag" -ExpectedCommit $first } 'HEAD'
    Assert-Rejected { & $guard -EventName push -EventRef "refs/tags/$tag" -ExpectedCommit $second } 'tag commit'
    $null = Invoke-Git @('tag', '-d', $tag)
    $null = Invoke-Git @('-c', 'tag.gpgsign=false', 'tag', $tag, $tree)
    Assert-Rejected { & $guard -EventName push -EventRef "refs/tags/$tag" -ExpectedCommit $second } 'resolve'
    Write-Output 'Release target fixture cases passed.'

    $workflow = Get-Content -Raw (Join-Path $root '.github/workflows/release.yml')
    Assert-True ($workflow.Contains('path: artifacts/desktop')) 'SBOM must scan the prepared Desktop payload.'
    foreach ($required in @('ref: ${{ github.sha }}', 'RELEASE_EVENT: ${{ github.event_name }}', 'RELEASE_REF: ${{ github.ref }}', 'RELEASE_COMMIT: ${{ github.sha }}', '-EventName $env:RELEASE_EVENT -EventRef $env:RELEASE_REF -ExpectedCommit $env:RELEASE_COMMIT', 'tag_name: ${{ steps.target.outputs.tag }}', 'RELEASE_TAG: ${{ steps.target.outputs.tag }}', 'releases/tags/$env:RELEASE_TAG', 'run: ./scripts/Build-Release.ps1')) {
        Assert-True ($workflow.Contains($required)) "Missing workflow contract: $required"
    }
    $last = -1
    foreach ($step in @('actions/checkout@', 'id: target', 'actions/setup-dotnet@', 'Install Inno Setup', 'run: ./scripts/Build-Release.ps1', 'Create portable archive', 'Attest release artifacts', 'Publish GitHub release', 'Append setup guide')) {
        $index = $workflow.IndexOf($step)
        Assert-True ($index -gt $last) "Workflow ordering invalid at $step."
        $last = $index
    }
    Assert-True ($workflow -notmatch 'SkipTests|SkipInstaller|continue-on-error|always\(\)|GITHUB_REF_NAME') 'Workflow contains a qualification bypass or unvalidated target.'
    Assert-True ([regex]::Matches($workflow, 'run: ./scripts/Build-Release.ps1').Count -eq 1) 'Exactly one qualification path is required.'
    Write-Output 'Release tooling characterization passed. Synthetic payloads are not distribution evidence.'
} finally {
    Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
}
