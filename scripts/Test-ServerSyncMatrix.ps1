param(
    [switch] $DownloadAndRun,
    [switch] $AcceptEula,
    [switch] $Discovery,
    [string[]] $Targets,
    [string] $OutputDirectory,
    [string] $SharedCacheDirectory,
    [string] $Java8Path,
    [string] $Java17Path,
    [string] $Java21Path,
    [string] $Java25Path,
    [string] $TestExecutable,
    [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $DownloadAndRun -or -not $AcceptEula) { throw 'Use -DownloadAndRun -AcceptEula to download files and run loopback servers and game clients.' }
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$matrix = @(
    @{ Id = 'fabric-1.16.5'; Minecraft = '1.16.5'; Loader = 'Fabric'; Version = '0.19.5'; Java = 8 },
    @{ Id = 'forge-1.16.5'; Minecraft = '1.16.5'; Loader = 'Forge'; Version = '36.2.34'; Java = 8 },
    @{ Id = 'forge-1.12.2'; Minecraft = '1.12.2'; Loader = 'Forge'; Version = '14.23.5.2859'; Java = 8 },
    @{ Id = 'fabric-1.20.1'; Minecraft = '1.20.1'; Loader = 'Fabric'; Version = '0.19.5'; Java = 17 },
    @{ Id = 'fabric-1.21.1'; Minecraft = '1.21.1'; Loader = 'Fabric'; Version = '0.19.5'; Java = 21 },
    @{ Id = 'quilt-1.21.1'; Minecraft = '1.21.1'; Loader = 'Quilt'; Version = '0.30.1'; Java = 21 },
    @{ Id = 'forge-1.20.1'; Minecraft = '1.20.1'; Loader = 'Forge'; Version = '47.4.23'; Java = 17 },
    @{ Id = 'neoforge-1.21.1'; Minecraft = '1.21.1'; Loader = 'NeoForge'; Version = '21.1.250'; Java = 21 },
    @{ Id = 'fabric-26.3'; Minecraft = '26.3'; Loader = 'Fabric'; Version = '0.19.5'; Java = 25 },
    @{ Id = 'neoforge-26.3'; Minecraft = '26.3'; Loader = 'NeoForge'; Version = '26.3.0.23-beta'; Java = 25 }
)
if ($Targets -and @($Targets | Where-Object { $_ -notin $matrix.Id }).Count -gt 0) { throw 'Unknown matrix target.' }
$selected = @($matrix | Where-Object { -not $Targets -or $_.Id -in $Targets })
$javaPaths = @{ 8 = $Java8Path; 17 = $Java17Path; 21 = $Java21Path; 25 = $Java25Path }
foreach ($major in @($selected.Java | Select-Object -Unique)) {
    if (-not $javaPaths[$major]) {
        $jdk = [Environment]::GetEnvironmentVariable('JAVA_HOME_' + $major + '_X64')
        if ($jdk) { $javaPaths[$major] = Join-Path $jdk 'bin/java.exe' }
    }
    if (-not $javaPaths[$major] -or -not (Test-Path -LiteralPath $javaPaths[$major] -PathType Leaf)) { throw ('Pass -Java' + $major + 'Path or set JAVA_HOME_' + $major + '_X64.') }
}
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root ('out/server-sync-matrix-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 6)) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'OutputDirectory must not exist.' }
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$results = @()
foreach ($case in $selected) {
    $caseRoot = Join-Path $OutputDirectory $case.Id
    $log = Join-Path $OutputDirectory ($case.Id + '.log')
    Write-Output ('RUN ' + $case.Id)
    $failure = $null
    try {
        $arguments = @{ DownloadAndRun = $true; AcceptEula = $true; SmokeRoot = $caseRoot; MinecraftVersion = $case.Minecraft
            Loader = $case.Loader; LoaderVersion = $case.Version; JavaPath = $javaPaths[$case.Java]; Configuration = $Configuration }
        if ($SharedCacheDirectory) { $arguments.SharedCacheDirectory = $SharedCacheDirectory }
        if ($TestExecutable) { $arguments.TestExecutable = $TestExecutable }
        if ($Discovery) { $arguments.Discovery = $true }
        & (Join-Path $PSScriptRoot 'Test-ServerSync.ps1') @arguments *> $log
    }
    catch { $failure = $_.Exception.Message }
    $report = Join-Path $caseRoot 'result.json'
    $result = if (Test-Path -LiteralPath $report) { Get-Content -LiteralPath $report -Raw | ConvertFrom-Json } else { $null }
    $passed = -not $failure -and $null -ne $result -and $result.passed
    $results += [ordered]@{ target = $case.Id; passed = [bool]$passed; result = $report; log = $log; error = $failure }
    ConvertTo-Json -InputObject @($results) -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'summary.json') -Encoding UTF8
    Write-Output (('{0} {1}' -f $(if ($passed) { 'PASS' } else { 'FAIL' }), $case.Id))
}
Write-Output (Join-Path $OutputDirectory 'summary.json')
if (@($results | Where-Object { -not $_.passed }).Count -gt 0) { throw 'Server sync matrix failed; see summary.json and per-target logs.' }
