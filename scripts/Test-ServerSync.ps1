param(
    [switch] $PrepareOnly,
    [switch] $DownloadAndRun,
    [switch] $AcceptEula,
    [string] $SmokeRoot,
    [string] $SharedCacheDirectory,
    [string] $ClientJar,
    [string] $MinecraftVersion = '1.21.1',
    [ValidateSet('Fabric', 'Quilt', 'Forge', 'NeoForge')] [string] $Loader = 'Fabric',
    [string] $LoaderVersion = '0.19.5',
    [string] $JavaPath = 'C:\Program Files\Eclipse Adoptium\jdk-21.0.11.10-hotspot\bin\java.exe',
    [string] $BridgePath,
    [string] $FixturePath,
    [string] $TestExecutable,
    [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $PrepareOnly -and (-not $DownloadAndRun -or -not $AcceptEula)) {
    throw 'Run with -DownloadAndRun -AcceptEula to start a local Minecraft server and game clients, or -PrepareOnly to prepare the server cache.'
}
if ($PrepareOnly -and $DownloadAndRun) { throw 'Choose either -PrepareOnly or -DownloadAndRun.' }

$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
if (-not $SharedCacheDirectory) { $SharedCacheDirectory = Join-Path $projectRoot 'out\integration\shared' }
$SharedCacheDirectory = (Resolve-Path -LiteralPath $SharedCacheDirectory).Path
$JavaPath = (Resolve-Path -LiteralPath $JavaPath).Path
$template = & (Join-Path $PSScriptRoot 'Prepare-ServerSyncServer.ps1') -MinecraftVersion $MinecraftVersion `
    -Loader $Loader -LoaderVersion $LoaderVersion -JavaPath $JavaPath -SharedCacheDirectory $SharedCacheDirectory
if ($PrepareOnly) {
    Write-Output ('Prepared ' + $Loader + ' ' + $MinecraftVersion + ' / ' + $LoaderVersion + ': ' + $template)
    return
}

if (-not $BridgePath -or -not $FixturePath) {
    $bridgeRoot = Join-Path $projectRoot 'mods\server-sync'
    $targets = Get-Content -LiteralPath (Join-Path $bridgeRoot 'targets.json') -Raw | ConvertFrom-Json
    $target = $targets | Where-Object { $_.minecraft -eq $MinecraftVersion -and $_.loader -eq $Loader } | Select-Object -First 1
    if (-not $target) { throw ('No bridge target for ' + $Loader + ' ' + $MinecraftVersion) }
    if (-not $BridgePath) { $BridgePath = Join-Path $bridgeRoot $target.file }
    if (-not $FixturePath) { $FixturePath = Join-Path $bridgeRoot $target.fixture }
}
$BridgePath = (Resolve-Path -LiteralPath $BridgePath).Path
$FixturePath = (Resolve-Path -LiteralPath $FixturePath).Path
if (-not $SmokeRoot) { $SmokeRoot = Join-Path $projectRoot ('out\server-sync-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 6)) }
$SmokeRoot = [IO.Path]::GetFullPath($SmokeRoot)
if (Test-Path -LiteralPath $SmokeRoot) {
    if (-not (Test-Path -LiteralPath $SmokeRoot -PathType Container) -or @(Get-ChildItem -LiteralPath $SmokeRoot -Force).Count -ne 0) {
        throw 'SmokeRoot must be a new, empty directory.'
    }
}
if ($SharedCacheDirectory.StartsWith($SmokeRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or
    $SharedCacheDirectory.Equals($SmokeRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'SharedCacheDirectory must be outside SmokeRoot.'
}
$serverDirectory = Join-Path $SmokeRoot 'server'
$dataDirectory = Join-Path $SmokeRoot 'data'
New-Item -ItemType Directory -Path $serverDirectory, $dataDirectory -Force | Out-Null
Get-ChildItem -LiteralPath $template -Force | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $serverDirectory -Recurse }
$sharedDirectory = Join-Path $dataDirectory 'shared'
New-Item -ItemType Directory -Path $sharedDirectory -Force | Out-Null
foreach ($directory in @('assets', 'libraries', 'versions', 'runtime')) {
    $cachedDirectory = Join-Path $SharedCacheDirectory $directory
    if (Test-Path -LiteralPath $cachedDirectory -PathType Container) {
        New-Item -ItemType Junction -Path (Join-Path $sharedDirectory $directory) -Target $cachedDirectory | Out-Null
    }
}
foreach ($filename in @('version_manifest.json', 'launcher_profiles.json')) {
    $cachedFile = Join-Path $SharedCacheDirectory $filename
    if (Test-Path -LiteralPath $cachedFile -PathType Leaf) { Copy-Item -LiteralPath $cachedFile -Destination (Join-Path $sharedDirectory $filename) }
}

$testArguments = @('--server-sync-smoke', '--accept-eula', '--smoke-root', $SmokeRoot,
    '--shared-cache', $SharedCacheDirectory, '--server-dir', $serverDirectory, '--java', $JavaPath,
    '--bridge', $BridgePath, '--fixture', $FixturePath, '--minecraft', $MinecraftVersion, '--loader', $Loader,
    '--loader-version', $LoaderVersion, '--server-arguments', (Join-Path $serverDirectory 'mechanica-server-arguments.json'))
if (-not $ClientJar) {
    $oldInstances = Join-Path (Split-Path -Parent $SharedCacheDirectory) 'instances'
    if (Test-Path -LiteralPath $oldInstances -PathType Container) {
        foreach ($directory in Get-ChildItem -LiteralPath $oldInstances -Directory) {
            $candidate = Join-Path $directory.FullName ('.minecraft\versions\' + $MinecraftVersion + '\' + $MinecraftVersion + '.jar')
            if (Test-Path -LiteralPath $candidate -PathType Leaf) { $ClientJar = $candidate; break }
        }
    }
}
if ($ClientJar) { $testArguments += @('--client-jar', (Resolve-Path -LiteralPath $ClientJar).Path) }
Push-Location -LiteralPath $projectRoot
try {
    $previousErrorAction = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        if ($TestExecutable) { & (Resolve-Path -LiteralPath $TestExecutable).Path @testArguments }
        else { & dotnet run --no-build --no-restore --project tests/MechanicaLauncher.Desktop.Tests -c $Configuration -- @testArguments }
        $testExitCode = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $previousErrorAction }
    if ($testExitCode -ne 0) { throw ('Server sync smoke failed. Artifacts: ' + $SmokeRoot) }
}
finally { Pop-Location }
