# Регистрация Microsoft для Mechanica

Собственного Application (client) ID пока нет. По умолчанию используется прежний legacy ID `00000000441cc96b`, включая новые входы. Приоритет настройки: `MECHANICA_MICROSOFT_CLIENT_ID` → ID, встроенный в сборку → legacy ID. Для текущего локального запуска собственная регистрация не требуется; ниже описано подключение своего приложения.

21.09.2026 проверены загрузка настоящей страницы входа Microsoft и обновление сохранённой пользовательской сессии через Microsoft → Xbox → XSTS → Minecraft. UUID совпал, обновлённый токен сохранён и принят Minecraft Services. Полный новый вход с вводом учётных данных в этой проверке не выполнялся.

## 1. Зарегистрировать приложение владельца

Открыть [Microsoft Entra → App registrations](https://entra.microsoft.com/#view/Microsoft_AAD_RegisteredApps/ApplicationsListBlade), войти своим аккаунтом и выбрать New registration.

| Поле | Значение |
| --- | --- |
| Name | `Mechanica Launcher` |
| Supported account types | `Personal Microsoft accounts only` / `Personal accounts only` |
| Platform | `Mobile and desktop applications` / `Public client/native` |
| Redirect URI | `https://login.microsoftonline.com/common/oauth2/nativeclient` |

Если redirect URI не был добавлен при создании, открыть Authentication → Add a platform → Mobile and desktop applications и выбрать этот URI. Скопировать **Application (client) ID** со страницы Overview. Object ID и Directory (tenant) ID для настройки лаунчера не подходят.

Используется authorization code flow с PKCE S256 и проверкой `state`; scopes — `XboxLive.signin offline_access`. Client secret не создавать: приложение является публичным desktop-клиентом.

Для регистрации нужен доступ к каталогу Entra с правом регистрации приложений. Если портал предлагает создать подписку или каталог, это отдельный шаг владельца; скрипты проекта не создают облачные ресурсы.

Источники: [регистрация приложения Microsoft](https://learn.microsoft.com/en-us/entra/identity-platform/quickstart-register-app), [authorization code flow и native redirect URI](https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-auth-code-flow).

## 2. Проверить допуск Minecraft Services

Успешная регистрация Entra сама по себе не подтверждает доступ к Minecraft Services. [Официальная статья Minecraft](https://help.minecraft.net/hc/en-us/articles/16254801392141) требует ручного добавления новых приложений в allow list. Статья и связанная с ней форма прочитаны 17.09.2026. Ошибка `Invalid app registration` после Xbox/XSTS означает, что вход в Minecraft не завершён.

В [официальной форме AppID Review](https://aka.ms/mce-reviewappid) есть вариант `New AppID for Approval`. Обязательны название, Application ID, Tenant ID, контактный email, ссылка на информацию о проекте, обоснование доступа и подтверждение ознакомления с EULA/Usage Guidelines. Явного требования популярности или минимального количества пользователей в опубликованной форме нет. Это не гарантирует одобрение заявки.

Форма прямо исключает приложения, обходящие проверки безопасности, авторизации или лицензии либо отключающие защитные функции. В Mechanica сейчас есть создание offline-профиля без входа Microsoft; перед заявкой нужно проверить и определить допустимое поведение этого режима. Соответствие этим условиям пока не подтверждено. Заявка от имени владельца не отправлялась.

## 3. Подключить ID

Для локальной проверки в PowerShell, подставив полученный GUID:

```powershell
$env:MECHANICA_MICROSOFT_CLIENT_ID = '<Application (client) ID>'
& .\out\stability-portable\MechanicaLauncher.exe
```

Перед запуском закрыть уже работающий лаунчер: его окружение не изменится от задания переменной в другой консоли.

Для распространяемой сборки добавить к обычной команде `dotnet publish` параметр `-p:MicrosoftClientId=<Application (client) ID>`. ID попадёт в метаданные сборки, и игрокам не потребуется переменная окружения. Для GitHub Actions используется repository variable `MICROSOFT_CLIENT_ID`. Стабильный тег без prerelease-суффикса (например, `v4.0.0`) требует собственного ID. Предварительные версии (например, `v4.0.0-beta.1`) допускают пустую переменную и используют прежний legacy ID; они публикуются как GitHub prerelease и не назначаются Latest. Любой заданный `MICROSOFT_CLIENT_ID` проверяется на корректный непустой GUID. Переменная окружения `MECHANICA_MICROSOFT_CLIENT_ID` имеет приоритет над ID, встроенным в сборку.

ID приложения публичный. Refresh/access tokens и client secret в исходники и параметры сборки не добавлять. ID сохраняется вместе с пользовательской сессией; обновление токена использует именно его, даже если настройки следующей сборки изменились.

## 4. Приёмка

- Вход с аккаунтом, имеющим Minecraft: Java Edition, заканчивается правильным именем и UUID.
- После перезапуска сессия обновляется с тем же Client ID и игра запускается с действующим Minecraft token.
- Отмена и отказ сервиса сохраняют текущий аккаунт; повторный вход возможен.
- Отсутствие профиля Xbox/Java и отказ регистрации приложения показывают понятную причину.

Локальные тесты подставляют HTTP-ответы и проверяют протокол, отмену и сообщения. Они не заменяют реальный вход и подтверждение доступа Minecraft Services.
