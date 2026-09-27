param(
    [switch] $DownloadAndRun,
    [switch] $PrepareOnly,
    [switch] $NoBuild,
    [string[]] $Targets,
    [string] $MatrixPath,
    [string] $DataDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $DownloadAndRun -and -not $PrepareOnly) { throw 'This check downloads real Modrinth mods into isolated test folders. Use -DownloadAndRun or -PrepareOnly.' }
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
if ($MatrixPath) {
    $matrix = Get-Content -LiteralPath (Resolve-Path -LiteralPath $MatrixPath).Path -Raw | ConvertFrom-Json
}
else {
    $matrix = [pscustomobject]@{
        schemaVersion = 1
        targets = @(
            @{ id='forge-1.12.2'; minecraft='1.12.2'; loader='Forge'; loaderVersion='14.23.5.2859'; projectId='u6dRKJwZ'; sourceVersionId='rhjEd6UL'; observedTargetVersionId='ys10FvNX' }
            @{ id='forge-1.16.5'; minecraft='1.16.5'; loader='Forge'; loaderVersion='36.2.34'; projectId='u6dRKJwZ'; sourceVersionId='5sqlmqOH'; observedTargetVersionId='DM1ByGMA' }
            @{ id='fabric-1.20.1'; minecraft='1.20.1'; loader='Fabric'; loaderVersion='0.19.5'; projectId='5ZwdcRci'; sourceVersionId='Us8JqrP9'; observedTargetVersionId='iwYUrQJO' }
            @{ id='forge-1.20.1'; minecraft='1.20.1'; loader='Forge'; loaderVersion='47.4.23'; projectId='uXXizFIs'; sourceVersionId='ULSumfl4'; observedTargetVersionId='DG5Fn9Sz' }
            @{ id='quilt-1.21.1'; minecraft='1.21.1'; loader='Quilt'; loaderVersion='0.30.1'; projectId='5ZwdcRci'; sourceVersionId='hZvFDqhH'; observedTargetVersionId='7XKnsIC8' }
            @{ id='neoforge-1.21.1'; minecraft='1.21.1'; loader='NeoForge'; loaderVersion='21.1.250'; projectId='uXXizFIs'; sourceVersionId='CnpoQxCx'; observedTargetVersionId='x7kQWVju' }
            @{ id='fabric-26.3'; minecraft='26.3'; loader='Fabric'; loaderVersion='0.19.5'; projectId='5ZwdcRci'; sourceVersionId='ugNfVpVH'; observedTargetVersionId='3MP9UR23' }
            @{ id='neoforge-26.3'; minecraft='26.3'; loader='NeoForge'; loaderVersion='26.3.0.23-beta'; projectId='5ZwdcRci'; sourceVersionId='CRMLDZp0'; observedTargetVersionId='4SovUFpn' }
        )
    }
}
if ($matrix.schemaVersion -ne 1 -or @($matrix.targets).Count -eq 0) { throw 'Expected matrix schemaVersion 1 with targets.' }
if ($Targets) {
    $unknown = @($Targets | Where-Object { $_ -notin $matrix.targets.id })
    if ($unknown.Count -gt 0) { throw ('Unknown mod update target: ' + ($unknown -join ', ')) }
    $matrix.targets = @($matrix.targets | Where-Object { $_.id -in $Targets })
}
if (-not $DataDirectory) {
    $DataDirectory = Join-Path $projectRoot ('out\mod-updates-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 6))
}
$DataDirectory = [IO.Path]::GetFullPath($DataDirectory)
if ((Test-Path -LiteralPath $DataDirectory) -and
    ((Get-Item -LiteralPath $DataDirectory).PSIsContainer -eq $false -or @(Get-ChildItem -LiteralPath $DataDirectory -Force).Count -gt 0)) {
    throw 'DataDirectory must be a new, empty directory.'
}
$metadataDirectory = Join-Path $projectRoot 'out\mod-update-metadata'
New-Item -ItemType Directory -Path $metadataDirectory -Force | Out-Null
$preparedMatrix = Join-Path $metadataDirectory ('matrix-' + [Guid]::NewGuid().ToString('N') + '.json')
$matrix | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $preparedMatrix -Encoding UTF8
Write-Output ('Matrix: ' + $preparedMatrix)
Write-Output ('Data: ' + $DataDirectory)
if ($PrepareOnly) { return }

$testProject = Join-Path $projectRoot 'tests\MechanicaLauncher.Core.Tests'
Push-Location -LiteralPath $projectRoot
try {
    if (-not $NoBuild) {
        $previousPreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue'
            & dotnet build $testProject -c Release --verbosity quiet
            $buildExit = $LASTEXITCODE
        }
        finally { $ErrorActionPreference = $previousPreference }
        if ($buildExit -ne 0) { throw 'Could not build mod update smoke tests.' }
    }
    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & dotnet run --no-build --no-restore --project $testProject -c Release -- --mod-updates-smoke $DataDirectory $preparedMatrix
        $smokeExit = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $previousPreference }
    if ($smokeExit -ne 0) { throw ('Mod update smoke failed or incomplete (exit ' + $smokeExit + '). Report: ' + (Join-Path $DataDirectory 'result.json')) }
    Write-Output ('PASS mod update smoke. Report: ' + (Join-Path $DataDirectory 'result.json'))
}
finally { Pop-Location }
