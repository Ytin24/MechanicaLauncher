param(
    [switch] $DownloadAndRun,
    [string] $DataDirectory
)

$ErrorActionPreference = 'Stop'
if (-not $DownloadAndRun) { throw 'This check downloads Minecraft and opens game windows. Run with -DownloadAndRun.' }
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
if (-not $DataDirectory) { $DataDirectory = Join-Path $projectRoot 'out\integration' }
$testProject = Join-Path $projectRoot 'tests\MechanicaLauncher.Core.Tests'
Push-Location -LiteralPath $projectRoot
try {
    & dotnet build $testProject -c Release --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Could not build compatibility checks.' }
    $matrix = @(
        @('1.12.2', 'None'),
        @('1.16.5', 'None'),
        @('1.20.1', 'None'),
        @('1.20.1', 'Forge', '47.4.23'),
        @('1.21.1', 'None'),
        @('1.21.1', 'Fabric', '0.19.5'),
        @('1.21.1', 'Quilt', '0.30.1'),
        @('1.21.1', 'NeoForge', '21.1.250'),
        @('26.3', 'None')
    )
    foreach ($case in $matrix) {
        & dotnet run --no-build --project $testProject -c Release -- --smoke $DataDirectory @case
        if ($LASTEXITCODE -ne 0) { throw ('Compatibility check failed: ' + ($case -join ' ')) }
    }
}
finally { Pop-Location }
