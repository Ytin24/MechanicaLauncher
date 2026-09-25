# Nitidus / ORDO

Локальный NuGet-feed для Windows x64, снимок `0.1.0-local.20260923.1` от 23.09.2026.

Источник: `C:\Users\ytin\source\repos\CSharpUiRenderer`, включая незакоммиченные изменения этого checkout на момент сборки. Внешняя публикация не выполнялась. Зафиксированы готовые бинарники; контрольные суммы пакетов — в `SHA256SUMS`, исходных файлов — в `SOURCE-SHA256SUMS`.

- `Nitidus.Core`: дерево UI, стили, данные, анимации, ScrollViewer для длинных форм.
- `Nitidus.Native`: Windows host, D3D11 / DirectWrite / WIC и native DLL для x64.
- `Ordo.Build`: компилятор `.ordo`, запускаемый MSBuild до C#; runtime-парсера нет.

Обычный `dotnet restore` использует корневой `NuGet.Config`; NuGet source mapping закрепляет эти имена за локальным feed. Сборка лаунчера не требует CMake, C++ toolchain или checkout фреймворка. Пакеты не опубликованы на nuget.org.

## Обновить снимок

Нужны исходники CSharpUiRenderer, .NET SDK 10, CMake и VS 2022 C++ toolchain.

```powershell
./scripts/Pack-Nitidus.ps1 -Source C:/Users/ytin/source/repos/CSharpUiRenderer -Version <новая-версия>
```

Использовать новую версию при каждом изменении бинарников. Обновить обе PackageReference в `src/MechanicaLauncher/MechanicaLauncher.csproj`, восстановить lock-файлы и запустить Core.Tests, Desktop.Tests, сборку и portable smoke. Снимок рассчитан на Windows x64; готовые ARM64/x86 DLL отсутствуют. Не поддерживаются SVG и UI Automation. WebP зависит от WIC-кодеков системы; ошибочное изображение пропускается.
