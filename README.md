# Mechanica Launcher

Лаунчер Minecraft для Windows 10/11 x64. Отдельные сборки, моды из Modrinth и запуск игры с нужной версией и загрузчиком.

[Скачать v4.0.0-beta.1](https://github.com/Ytin24/MechanicaLauncher/releases/tag/v4.0.0-beta.1)

![Главная Mechanica Launcher с обложкой выбранной сборки](docs/images/home.png)

## Скачать

Бета-версия для Windows x64:

- **[Установщик](https://github.com/Ytin24/MechanicaLauncher/releases/download/v4.0.0-beta.1/MechanicaLauncher-4.0.0-beta.1-setup.exe)** — установи лаунчер и запускай его из меню «Пуск».
- **[Portable ZIP](https://github.com/Ytin24/MechanicaLauncher/releases/download/v4.0.0-beta.1/MechanicaLauncher-x64-portable.zip)** — распакуй архив и открой `MechanicaLauncher.exe`. Сборки и настройки хранятся в папке `data` рядом с лаунчером.

Отдельно устанавливать .NET не нужно. Известные ограничения указаны в описании бета-релиза.

## Возможности

- Отдельные сборки со своими мирами, модами и настройками. Импорт и экспорт `.mrpack`.
- Minecraft без модов, Fabric, Quilt, Forge и NeoForge. Подбор и загрузка нужной Java.
- Каталог Modrinth: моды, модпаки, шейдеры, ресурспаки и датапаки. Описания, скриншоты и фильтр по версии Minecraft.
- Очередь загрузок с отменой и повтором, проверка файлов и отчёты о вылетах.
- Любимые серверы, галерея игровых скриншотов, обложки сборок и редактор скина.
- Учётные записи Microsoft и локальные профили, Discord Rich Presence, сворачивание в трей.
- Светлая и тёмная темы, русский и английский интерфейс, отключаемые анимации.

## В текущем исходном коде

Изменения после `v4.0.0-beta.1`:

- Распознавание установленных модов по содержимому JAR, включая переименованные и отключённые файлы. Проверка совместимых обновлений Modrinth с резервными копиями.
- [Mechanica Server Sync](https://github.com/Ytin24/MechanicaServerSync): докачка разрешённых модов сервера, перезапуск игры и повторное подключение. Включается галочкой в настройках конкретной сборки.

[Проверенные версии и ограничения](COMPATIBILITY.md) · [Настройка синхронизации](docs/server-mod-sync.md)

## Сборки

![Библиотека сборок в светлой теме](docs/images/instances.png)

## Параметры игры

![Память и размер окна в настройках сборки](docs/images/instance-settings.png)

Обложка на первом снимке — [Minecraft.net](https://www.minecraft.net/en-us/about-minecraft).

## Сборка из исходников

Клонировать с `git clone https://github.com/Ytin24/MechanicaLauncher.git`. Из папки репозитория выполнить `git submodule update --init --recursive -- mods/server-sync` — эта же команда обновляет мод в существующей копии.

На Windows нужны .NET SDK 9 и 10, JDK 8, 17, 21 и 25. Сначала выполнить `./scripts/Build-ServerSyncBridge.ps1`, затем `dotnet build tests/MechanicaLauncher.Desktop.Tests -c Release`. Настройка Java и варианты мода описаны в [его README](https://github.com/Ytin24/MechanicaServerSync#сборка). Полная сборка, тесты и упаковка выполняются в [CI](.github/workflows/build-release.yml).

[Сообщить об ошибке](https://github.com/Ytin24/MechanicaLauncher/issues)
