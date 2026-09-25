$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\MechanicaLauncher.Desktop.Tests\MechanicaLauncher.Desktop.Tests.csproj'
& dotnet run --project $project -c Release
if ($LASTEXITCODE -ne 0) { throw 'Nitidus UI regression checks failed.' }
