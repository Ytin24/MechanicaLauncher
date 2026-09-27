param(
    [switch] $DownloadAndRun,
    [Parameter(Mandatory = $true)] [string] $PackPath,
    [Parameter(Mandatory = $true)] [string] $VersionMetadataPath,
    [string] $DataDirectory,
    [string] $SharedCacheDirectory,
    [string] $ContentCacheDirectory
)

$ErrorActionPreference = 'Stop'
if (-not $DownloadAndRun) { throw 'This check downloads pack files, Minecraft and its exact loader, then opens two game windows. Run with -DownloadAndRun.' }
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$pack = (Resolve-Path -LiteralPath $PackPath).Path
$metadata = (Resolve-Path -LiteralPath $VersionMetadataPath).Path
if (-not (Test-Path -LiteralPath $pack -PathType Leaf) -or -not (Test-Path -LiteralPath $metadata -PathType Leaf)) {
    throw 'PackPath and VersionMetadataPath must be existing local files.'
}
if (-not $DataDirectory) { $DataDirectory = Join-Path $projectRoot ('out\modpack-smoke\' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 6)) }
$DataDirectory = [IO.Path]::GetFullPath($DataDirectory)
if ((Test-Path -LiteralPath $DataDirectory) -and ((Get-Item -LiteralPath $DataDirectory).PSIsContainer -eq $false -or @(Get-ChildItem -LiteralPath $DataDirectory -Force).Count -gt 0)) {
    throw 'DataDirectory must be a new, empty directory.'
}
if ($SharedCacheDirectory) { $SharedCacheDirectory = [IO.Path]::GetFullPath($SharedCacheDirectory) }
if ($ContentCacheDirectory) {
    $ContentCacheDirectory = (Resolve-Path -LiteralPath $ContentCacheDirectory).Path
    if (-not $SharedCacheDirectory) { $SharedCacheDirectory = Join-Path $DataDirectory 'shared' }
}
$testProject = Join-Path $projectRoot 'tests\MechanicaLauncher.Core.Tests'
$testArguments = @('--modpack-smoke', $DataDirectory, $pack, $metadata)
if ($SharedCacheDirectory) { $testArguments += $SharedCacheDirectory }
if ($ContentCacheDirectory) { $testArguments += $ContentCacheDirectory }
Push-Location -LiteralPath $projectRoot
try {
    & dotnet build $testProject -c Release --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Could not build modpack checks.' }
    & dotnet run --no-build --project $testProject -c Release -- @testArguments
    if ($LASTEXITCODE -ne 0) { throw ('Modpack check failed. Report: ' + (Join-Path $DataDirectory 'result.json')) }
}
finally { Pop-Location }
