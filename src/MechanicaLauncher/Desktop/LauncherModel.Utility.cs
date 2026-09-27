using System.Net.Http.Headers;
using System.Net;
using System.Text.RegularExpressions;
using MechanicaLauncher.Core.Auth;
using MechanicaLauncher.Core.Servers;
using MechanicaLauncher.Core.Discord;
using MechanicaLauncher.Core.Updates;

namespace MechanicaLauncher.Desktop;

public sealed partial class LauncherModel
{
    public ObservableCollection<ItemModel> DownloadItems { get; } = [];
    public ObservableCollection<ItemModel> ServerItems { get; } = [];
    public string DownloadFilter { get; private set; } = "all";
    public string DownloadsStatus => Sessions.Downloads.HasPending ? T("Загрузки продолжаются при переходе между экранами.", "Downloads continue while you browse.") : T("Нет активных загрузок", "No active downloads");
    public void FilterDownloads(string filter) { DownloadFilter = filter; RefreshDownloads(); Changed(); }
    public void RefreshDownloads()
    {
        var jobs = Sessions.Downloads.Jobs.Reverse().Where(j => DownloadFilter switch { "active" => j.State is DownloadState.Queued or DownloadState.Running, "failed" => j.State is DownloadState.Failed or DownloadState.Cancelled, _ => true }).ToArray();
        var ids = jobs.Select(j => j.Id.ToString()).ToHashSet();
        foreach (var old in DownloadItems.Where(i => !ids.Contains(i.Id)).ToArray()) DownloadItems.Remove(old);
        for (int index = 0; index < jobs.Length; index++)
        {
            var job = jobs[index];
            var snapshot = job.Snapshot();
            var row = DownloadItems.FirstOrDefault(i => i.Id == job.Id.ToString());
            if (row == null) { row = new() { Id = job.Id.ToString() }; DownloadItems.Insert(index, row); }
            else if (DownloadItems.IndexOf(row) != index) DownloadItems.Move(DownloadItems.IndexOf(row), index);
            row.Title = snapshot.Title;
            row.Meta = L("downloads." + snapshot.State.ToString().ToLowerInvariant()) + " · " + Size(snapshot.Received) + (snapshot.Total > 0 ? " / " + Size(snapshot.Total) : "") + (snapshot.BytesPerSecond > 0 ? " · " + Size(snapshot.BytesPerSecond) + "/s" : "");
            row.Description = snapshot.Error ?? string.Join(" · ", snapshot.Files.Where(f => !f.Complete).Take(2).Select(f => f.Name));
            row.HasProgress = snapshot.State == DownloadState.Running;
            row.Progress = snapshot.Total > 0 ? (double)snapshot.Received / snapshot.Total * 100 : 0;
            row.Primary = snapshot.State is DownloadState.Running or DownloadState.Queued ? T("Отменить", "Cancel") : snapshot.CanRetry ? T("Повторить", "Retry") : "";
            row.Action = () => { if (job.State is DownloadState.Running or DownloadState.Queued) job.Cancel(); else Sessions.Downloads.Retry(job); RefreshDownloads(); };
            row.Secondary = snapshot.InstanceId != null && Instances.GetInstance(snapshot.InstanceId) != null ? T("К сборке", "Open instance") : "";
            row.Action2 = () => { if (snapshot.InstanceId != null) EditInstance(snapshot.InstanceId); };
            row.Changed();
        }
    }
    private static string Size(double bytes) => bytes >= 1_073_741_824 ? $"{bytes / 1_073_741_824:0.0} GB" : bytes >= 1_048_576 ? $"{bytes / 1_048_576:0.0} MB" : bytes >= 1024 ? $"{bytes / 1024:0.0} KB" : $"{bytes:0} B";
    public void CancelDownloads() => Run(async () => { if (await Confirm(T("Отменить загрузки?", "Cancel downloads?"), T("Готовые файлы сохранятся.", "Completed files will be kept."), T("Отменить все", "Cancel all"))) Sessions.Downloads.CancelAll(); });

    public void RefreshServers()
    {
        ServerItems.Clear();
        foreach (var server in new FavoriteServers().Load())
        {
            var instance = Instances.GetInstance(server.InstanceId);
            ServerItems.Add(new()
            {
                Id = server.Id, Title = server.Name, Meta = server.Address,
                Description = instance == null ? T("Выбери сборку в настройках сервера", "Choose an instance in server settings") : instance.Name + " · " + InstanceLabel(instance),
                Primary = T("Подключиться", "Connect"), Secondary = T("Изменить", "Edit"), Tertiary = T("Удалить", "Remove"),
                Action = () => ConnectServer(server),
                Action2 = () => EditServer(server), Action3 = () => Run(async () => { if (await Confirm(T("Удалить сервер?", "Remove server?"), server.Name, T("Удалить", "Remove"), destructive: true)) { var store = new FavoriteServers(); store.Save(store.Load().Where(s => s.Id != server.Id)); RefreshServers(); } })
            });
        }
        Changed();
    }
    public void AddServer() => EditServer(null);
    private void EditServer(FavoriteServer? server)
    {
        if (disposed || DialogOpen || TLauncherBlocked) return;
        if (SelectedInstance == null) { Notice(T("Сначала выбери сборку в шапке.", "Select an instance in the header first."), true); return; }
        string targetId = server != null && Instances.GetInstance(server.InstanceId) != null ? server.InstanceId : SelectedInstance.Id;
        var addressField = new FieldModel("address", T("Адрес", "Address"), server?.Address ?? "");
        var syncField = new FieldModel("syncUrl", T("Ссылка на список модов (необязательно)", "Mod list URL (optional)"), server?.SyncManifestUrl ?? "");
        var automatic = new FieldModel("autoSync", T("Автодокачка модов", "Download server mods automatically"))
        {
            IsToggle = true, IsChecked = server is { AutoSync: true } && targetId == server.InstanceId
        };
        addressField.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(FieldModel.Value)) automatic.IsChecked = false; };
        syncField.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(FieldModel.Value)) automatic.IsChecked = false; };
        BeginDialog(server == null ? T("Добавить сервер", "Add server") : T("Изменить сервер", "Edit server"),
            T("Сборка: ", "Instance: ") + Instances.GetInstance(targetId)?.Name, T("Сохранить", "Save"), () =>
            {
                string name = Field("name"), address = Field("address"), syncUrl = Field("syncUrl");
                if (name.Length == 0) throw new InvalidDataException(T("Введи название сервера.", "Enter a server name."));
                if (!FavoriteServers.TryParseAddress(address, out var host, out int port)) throw new InvalidDataException(T("Проверь адрес: host или host:port.", "Check the address: host or host:port."));
                if (Instances.GetInstance(targetId) == null) throw new InvalidDataException(T("Выбранная сборка удалена. Выбери другую.", "The selected instance was deleted. Choose another."));
                if (syncUrl.Length > 0 && !FavoriteServers.IsValidSyncUrl(syncUrl, true))
                    throw new InvalidDataException(T("Нужна HTTPS-ссылка на список модов. HTTP доступен только для локального сервера.", "Use an HTTPS mod list URL. HTTP is available only for a local server."));
                if (automatic.IsChecked && syncUrl.Length == 0)
                    throw new InvalidDataException(T("Укажи ссылку на список модов для автодокачки.", "Enter a mod list URL to enable automatic downloads."));
                Uri? source = syncUrl.Length == 0 ? null : new Uri(syncUrl);
                bool allowLocal = source != null && IPAddress.TryParse(source.IdnHost.Trim('[', ']'), out var addressIp) && IPAddress.IsLoopback(addressIp);
                var store = new FavoriteServers(); var all = store.Load().Where(s => s.Id != server?.Id).ToList();
                all.Add(new(server?.Id ?? Guid.NewGuid().ToString("N"), name, host, port, targetId)
                {
                    SyncManifestUrl = source?.AbsoluteUri, AutoSync = automatic.IsChecked, AllowLocalSync = allowLocal
                });
                store.Save(all); RefreshServers(); return Task.CompletedTask;
            }, new("name", T("Название", "Name"), server?.Name ?? ""), addressField, syncField, automatic);
        foreach (var instance in Instances.GetAllInstances())
            DialogChoices.Add(new()
            {
                Id = instance.Id, Title = instance.Name, Meta = InstanceLabel(instance), Selected = instance.Id == targetId,
                Action = () =>
                {
                    if (targetId != instance.Id) automatic.IsChecked = false;
                    targetId = instance.Id;
                    DialogBody = T("Сборка: ", "Instance: ") + instance.Name;
                    foreach (var row in DialogChoices) { row.Selected = row.Id == targetId; row.Changed(); }
                    Changed();
                }
            });
        Changed();
    }
    public void CheckServers() => Run(() => WithBusy(async () =>
    {
        var token = PageToken;
        foreach (var server in new FavoriteServers().Load())
        {
            token.ThrowIfCancellationRequested();
            var row = ServerItems.FirstOrDefault(i => i.Id == server.Id); if (row == null) continue;
            try
            {
                var status = await new ServerStatusClient().QueryAsync(server.Host, server.Port, token);
                token.ThrowIfCancellationRequested(); row.Meta = server.Address + $" · {status.Online}/{status.Maximum} · {status.LatencyMs} ms"; row.Description = status.Description + " · " + status.Version;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { row.Meta = server.Address + " · " + T("Недоступен", "Unavailable"); }
            row.Changed();
        }
    }));

    public bool LightTheme { get => Settings.Theme == "Light"; set { Settings.Theme = value ? "Light" : "Dark"; Settings.Save(); Changed(); } }
    public bool Animations { get => Settings.Animations; set { Settings.Animations = value; Settings.Save(); Changed(); } }
    public bool CloseOnLaunch { get => Settings.CloseOnLaunch; set { Settings.CloseOnLaunch = value; Settings.Save(); Changed(); } }
    public bool CloseToTray { get => Settings.CloseToTray; set { Settings.CloseToTray = value; Settings.Save(); Changed(); } }
    public bool MinimizeToTray { get => Settings.MinimizeToTray; set { Settings.MinimizeToTray = value; Settings.Save(); Changed(); } }
    public bool ShowSnapshots { get => Settings.ShowSnapshots; set { Settings.ShowSnapshots = value; Settings.Save(); Changed(); } }
    public bool DiscordEnabled { get => Settings.DiscordRpc; set { Settings.DiscordRpc = value; SaveDiscord(); } }
    public bool DiscordServer { get => Settings.DiscordShowServer; set { Settings.DiscordShowServer = value; SaveDiscord(); } }
    public bool DiscordDimension { get => Settings.DiscordShowDimension; set { Settings.DiscordShowDimension = value; SaveDiscord(); } }
    public bool DiscordAchievements { get => Settings.DiscordShowAchievements; set { Settings.DiscordShowAchievements = value; SaveDiscord(); } }
    public bool DiscordMods { get => Settings.DiscordShowMods; set { Settings.DiscordShowMods = value; SaveDiscord(); } }
    public string DiscordStatus => L("discord." + (Sessions.Discord.Snapshot.Status switch { DiscordConnectionStatus.Connected => "connected", DiscordConnectionStatus.Connecting => "connecting", DiscordConnectionStatus.WaitingForDiscord => "waiting", DiscordConnectionStatus.Error => "error", _ => "disabled" }));
    public string DiscordPreview => Sessions.Discord.Snapshot.Details + "\n" + Sessions.Discord.Snapshot.State;
    public string LanguageLabel => Locale.CurrentLanguage == "ru" ? "Русский" : "English";
    public string VersionLabel => "Mechanica · Nitidus / ORDO";
    public string UpdateStatus { get; private set; } = "";
    private UpdateInfo? update;
    public bool HasUpdate => update?.IsAvailable == true;
    public bool EventActive => Sessions.Events.IsEventMode;
    public string EventName => Sessions.Events.Active?.Name ?? "";
    private void SaveDiscord() { Settings.Save(); Sessions.Discord.Configure(Settings, Locale.CurrentLanguage); Changed(); }
    public void ReconnectDiscord() { Sessions.Discord.Reconnect(); Changed(); }
    public void ChooseLanguage() => ShowChoices(T("Язык интерфейса", "Language"), new[] { ("ru", "Русский"), ("en", "English") }.Select(language => new ItemModel
    {
        Title = language.Item2, Action = () => { Settings.Language = language.Item1; Settings.Save(); Locale.Init(language.Item1); CloseDialog(); Sessions.Discord.Configure(Settings, Locale.CurrentLanguage); RefreshInstances(); Changed(); }
    }));
    public void CheckUpdates() => Run(() => WithBusy(async () => { update = await UpdateChecker.CheckAsync(); UpdateStatus = update.IsAvailable ? T("Доступна версия ", "Available version ") + update.LatestVersion : T("Новых обновлений нет", "No updates available"); Changed(); }));
    public void OpenUpdate() { if (update?.DownloadUrl != null) Platform.OpenUrl(update.DownloadUrl); }
    public void OpenDataFolder() => Platform.OpenPath(LauncherPaths.DataDirectory);
    public void LeaveEvent() { Sessions.Events.Clear(); Settings.ActiveEventUrl = null; Settings.Save(); Changed(); }

    public string OfflineName { get; set; } = "";
    public bool MicrosoftAccount => Settings.AuthMode == "microsoft";
    public ImageSource Avatar { get; private set; } = ImageSource.Empty;
    public ImageSource SkinPreview { get; private set; } = ImageSource.Empty;
    public string SkinVariant { get; private set; } = "classic";
    private static readonly HttpClient accountHttp = new() { Timeout = TimeSpan.FromSeconds(45) };
    private void LoadAccount()
    {
        OfflineName = Settings.Username;
        Avatar = ImageSource.Empty; SkinPreview = ImageSource.Empty;
        if (MicrosoftAccount) Run(async () =>
        {
            var token = PageToken;
            Avatar = await MediaCache.LoadRemote("https://mc-heads.net/avatar/" + Uri.EscapeDataString(Settings.Uuid) + "/128", token);
            token.ThrowIfCancellationRequested(); Changed();
            SkinPreview = await MediaCache.LoadRemote("https://mc-heads.net/body/" + Uri.EscapeDataString(Settings.Uuid) + "/256", token);
            token.ThrowIfCancellationRequested(); Changed();
        });
        Changed();
    }
    public void SaveOffline() => Run(() =>
    {
        string name = OfflineName.Trim();
        if (!Regex.IsMatch(name, "^[A-Za-z0-9_]{3,16}$")) throw new InvalidDataException(T("Ник: 3–16 латинских букв, цифр или _.", "Username: 3–16 Latin letters, digits or _."));
        Settings.Username = name; Settings.AuthMode = "offline"; Settings.Uuid = "0"; Settings.AccessToken = "0"; Settings.MsRefreshToken = ""; Settings.MsClientId = ""; Settings.Save(); LoadAccount(); return Task.CompletedTask;
    });
    public void SignOut() => Run(async () =>
    {
        if (!await Confirm(T("Выйти из Microsoft?", "Sign out of Microsoft?"), T("Сборки и миры останутся на месте.", "Your instances and worlds will stay on this computer."), T("Выйти", "Sign out"))) return;
        Settings.AuthMode = "offline"; Settings.AccessToken = "0"; Settings.MsRefreshToken = ""; Settings.MsClientId = ""; Settings.Uuid = "0"; Settings.Save(); LoadAccount();
    });
    public void SignIn() => Run(() => WithBusy(async () =>
    {
        var auth = MicrosoftAuth.CreateForSignIn(); var request = auth.BeginSignIn();
        var redirect = Platform.SignIn(request);
        if (redirect == null) return;
        var result = await auth.CompleteAsync(request, redirect);
        Settings.AuthMode = "microsoft"; Settings.Username = result.Username; Settings.Uuid = result.Uuid;
        Settings.AccessToken = result.AccessToken; Settings.MsRefreshToken = result.RefreshToken ?? ""; Settings.MsClientId = auth.ClientId; Settings.Save();
        LoadAccount(); Notice(T("Вход выполнен", "Signed in"));
    }));
    public void ToggleSkinVariant() { SkinVariant = SkinVariant == "classic" ? "slim" : "classic"; Changed(); }
    public void UploadSkin() => Run(async () =>
    {
        if (!MicrosoftAccount) throw new InvalidOperationException(T("Сначала войди через Microsoft.", "Sign in with Microsoft first."));
        var file = Platform.PickFile("Minecraft skin|*.png"); if (file == null) return;
        await WithBusy(() => UploadSkinBytes(File.ReadAllBytes(file)));
    });
    private async Task UploadSkinBytes(byte[] bytes)
    {
        if (!MicrosoftAccount) throw new InvalidOperationException(T("Сначала войди через Microsoft.", "Sign in with Microsoft first."));
        using (var stream = new MemoryStream(bytes)) using (var image = System.Drawing.Image.FromStream(stream))
            if (image.Width != 64 || image.Height is not (32 or 64)) throw new InvalidDataException(T("Размер скина: 64×64 или 64×32 PNG.", "Skin must be a 64×64 or 64×32 PNG."));
        using var form = new MultipartFormDataContent(); form.Add(new StringContent(SkinVariant), "variant");
        var file = new ByteArrayContent(bytes); file.Headers.ContentType = new("image/png"); form.Add(file, "file", "skin.png");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.minecraftservices.com/minecraft/profile/skins") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Settings.AccessToken);
        using var response = await accountHttp.SendAsync(request); response.EnsureSuccessStatusCode();
        SkinPreview = MediaCache.LoadBytes(bytes); Notice(T("Скин загружен", "Skin uploaded"));
    }
}
