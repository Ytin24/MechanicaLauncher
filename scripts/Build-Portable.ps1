param([string]$DestinationDirectory = [Environment]::GetFolderPath('DesktopDirectory'))
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$destination = (Resolve-Path -LiteralPath $DestinationDirectory).Path
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$buildRoot = Join-Path $projectRoot ('out\portable-' + $stamp + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$package = Join-Path $buildRoot 'package'
$app = Join-Path $package 'MechanicaLauncher'
New-Item -ItemType Directory -Path $app | Out-Null
& dotnet publish (Join-Path $projectRoot 'src\MechanicaLauncher\MechanicaLauncher.csproj') -c Release -p:Platform=x64 -p:SelfContained=true -p:PublishTrimmed=false -p:RestoreLockedMode=true -p:DebugType=None -p:DebugSymbols=false -r win-x64 --output $app --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Portable publish failed' }
if (-not (Test-Path -LiteralPath (Join-Path $app 'MechanicaLauncher.exe'))) { throw 'Launcher executable is missing' }
[IO.File]::WriteAllText((Join-Path $app 'portable.flag'), '', [Text.UTF8Encoding]::new($false))
$readme = @'
Mechanica Launcher — тестовая портативная сборка для Windows x64

1. Распакуй всю папку MechanicaLauncher из ZIP в доступное для записи место.
2. Запусти MechanicaLauncher.exe. Устанавливать .NET отдельно не нужно.
3. Сборки, настройки, аккаунты и журналы появятся в папке data рядом с EXE.
   Переносить лаунчер нужно целиком вместе с data. portable.flag включает этот режим.

Закрытие крестиком убирает лаунчер в трей. Полный выход: значок в трее → Выход.
Для загрузок и входа нужен интернет. Вход Microsoft использует WebView2 Runtime.

При обнаружении TLauncher доступна его очистка: собственные папки, Java, кэш,
ярлыки, автозапуск и подтверждённые записи Windows. Удаление — по кнопке «Удалить».
Резервные копии: data\backups\tlauncher. TLegacy и игровые данные сохраняются.

Для теста: создай сборку, запусти игру, установи мод, проверь настройки и трей.
При ошибке сохрани текст сообщения и журнал из data\logs.
В архив не включены готовые аккаунты, токены, миры или личные настройки.
'@
[IO.File]::WriteAllText((Join-Path $app 'ПРОЧИТАЙ.txt'), $readme + "`r`n`r`nСборка: " + $stamp + "`r`n", [Text.UTF8Encoding]::new($true))
$privateFiles = @(Get-ChildItem -LiteralPath $app -File -Recurse | Where-Object {
    $_.Name -in @('settings.json', 'settings.json.bak', 'accounts.json', 'pending_connect.txt') -or $_.Extension -eq '.pdb' -or
    $_.FullName.StartsWith((Join-Path $app 'data') + '\', [StringComparison]::OrdinalIgnoreCase)
})
if ($privateFiles.Count -gt 0) { throw 'Unexpected personal data or debugging symbols in portable output' }
$archive = Join-Path $destination ('MechanicaLauncher-portable-win-x64-' + $stamp + '.zip')
if (Test-Path -LiteralPath $archive) { throw ('Archive already exists: ' + $archive) }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($package, $archive, [IO.Compression.CompressionLevel]::Optimal, $false)
$result = [pscustomobject]@{
    Archive = $archive
    Bytes = (Get-Item -LiteralPath $archive).Length
    SHA256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
    Application = $app
    BuildDirectory = $buildRoot
}
$result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $projectRoot 'out\portable-latest.json') -Encoding UTF8
$result
