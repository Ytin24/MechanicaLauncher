using MechanicaLauncher.Core.Presets;
using MechanicaLauncher.Core.Servers;

namespace MechanicaLauncher.Desktop;

// Shared instance links: a creator puts one link under a video, the player clicks it,
// confirms once, and the launcher downloads and selects a ready instance.
public sealed partial class LauncherModel
{
    public void ImportMenu()
    {
        if (!AllowCreate) return;
        ShowChoices(T("Импорт сборки", "Import instance"), new ItemModel[]
        {
            new() { Title = T("По ссылке", "From a link"), Description = T("Ссылка из описания видео, страница Modrinth или прямая ссылка на .mrpack.", "A link from a video description, a Modrinth page or a direct .mrpack link."), Action = () => { CloseDialog(); OpenPresetLink(); } },
            new() { Title = T("Файл .mrpack", "A .mrpack file"), Description = T("Сборка, сохранённая на этом компьютере.", "An instance saved on this computer."), Action = () => { CloseDialog(); ImportPack(); } }
        });
        DialogSearch = false; Changed();
    }

    public void OpenPresetLink()
    {
        if (!AllowCreate) return;
        string clipboard = Platform.ClipboardText() is { } text && PresetLink.TryParse(text, out _) ? text.Trim() : "";
        BeginDialog(T("Сборка по ссылке", "Instance from a link"),
            T("Вставь ссылку из описания видео. Перед установкой лаунчер покажет, откуда сборка.", "Paste the link from the video description. You will see where the instance comes from before anything installs."),
            T("Продолжить", "Continue"), () =>
            {
                if (!PresetLink.TryParse(Field("link"), out var link))
                    throw new InvalidDataException(T("Не похоже на ссылку на сборку. Подойдёт ссылка Mechanica, страница модпака на Modrinth или прямая HTTPS-ссылка на .mrpack.", "This does not look like an instance link. Use a Mechanica link, a Modrinth modpack page or a direct HTTPS .mrpack link."));
                // The link dialog closes after this action returns; the confirmation opens right after.
                Run(async () => { await Task.Yield(); await OpenPreset(link); });
                return Task.CompletedTask;
            }, new FieldModel("link", T("Ссылка", "Link"), clipboard));
    }

    internal async Task OpenPreset(PresetLink link)
    {
        RequireEventPermission(AllowCreate);
        var installer = new PresetInstaller(modrinth: CatalogClient);
        PresetPreview preview;
        try { preview = await installer.PreviewAsync(link, PageToken); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(T("Не удалось открыть сборку по ссылке: ", "Could not open the shared instance: ") + ex.Message, ex);
        }
        var lines = new List<string> { T("Источник: ", "Source: ") + preview.Source };
        if (!string.IsNullOrWhiteSpace(preview.Description)) lines.Add(preview.Description.Trim());
        if (link.Server != null) lines.Add(T("Сервер: ", "Server: ") + link.Server);
        lines.Add(T("Сборка установится отдельно и не затронет другие миры. Моды — это программы, поэтому ставь сборки только от авторов, которым доверяешь.",
            "The instance installs separately and leaves your other worlds alone. Mods are programs, so only install instances from creators you trust."));
        if (!await Confirm(T($"Установить «{preview.Title}»?", $"Install “{preview.Title}”?"), string.Join("\n\n", lines), T("Установить", "Install"))) return;
        RequireEventPermission(AllowCreate);
        Sessions.Enqueue(preview.Title, null, async token =>
        {
            RequireEventPermission(AllowCreate);
            var result = await installer.InstallAsync(link, Instances, token);
            DownloadQueue.Current!.ResultInstanceId = result.Instance.Id;
            Dispatch(() => { if (!disposed) PresetReady(result); });
        });
        Downloads();
    }

    private void PresetReady(PresetInstallResult result)
    {
        var instance = result.Instance;
        if (result.Server is { } server && FavoriteServers.TryParseAddress(server.Address, out var host, out int port))
        {
            var store = new FavoriteServers();
            var all = store.Load().ToList();
            if (!all.Any(s => s.InstanceId == instance.Id && s.Host.Equals(host, StringComparison.OrdinalIgnoreCase) && s.Port == port))
            {
                all.Add(new(Guid.NewGuid().ToString("N"), server.Name, host, port, instance.Id) { SyncManifestUrl = server.SyncUrl });
                store.Save(all); RefreshServers();
            }
        }
        if (Instances.GetInstance(instance.Id) == null) return;
        SetInstance(instance.Id);
        if (Page == "downloads") Home();
        var summary = result.Summary;
        string worlds = summary.Worlds.Count == 0 ? "" : T(" · мир: ", " · world: ") + string.Join(", ", summary.Worlds);
        string author = summary.Details.Author == null ? "" : T(" от ", " by ") + summary.Details.Author;
        Notice(T($"«{instance.Name}»{author} готова: Minecraft {summary.Minecraft}, модов {summary.Mods}{worlds}. Жми «Играть».",
            $"“{instance.Name}”{author} is ready: Minecraft {summary.Minecraft}, {summary.Mods} mods{worlds}. Press Play."));
    }

    // Creator side: turn an uploaded pack into a link that is clickable in a YouTube description.
    private void SharePresetLink(GameInstance instance)
    {
        BeginDialog(T("Ссылка для зрителей", "Link for your audience"),
            T($"1. Экспортируй «{instance.Name}» в .mrpack — миры из saves попадут внутрь.\n2. Загрузи файл на Modrinth или на хостинг с прямой HTTPS-ссылкой.\n3. Вставь адрес сюда и получи ссылку для описания видео.",
              $"1. Export “{instance.Name}” as .mrpack; worlds in saves are included.\n2. Upload it to Modrinth or any host with a direct HTTPS link.\n3. Paste the address here to get a link for your video description."),
            T("Скопировать ссылку", "Copy link"), () =>
            {
                if (!PresetLink.TryParse(Field("source"), out var link))
                    throw new InvalidDataException(T("Нужна страница модпака на Modrinth или прямая HTTPS-ссылка на .mrpack.", "Use a Modrinth modpack page or a direct HTTPS .mrpack link."));
                string server = Field("server");
                if (server.Length > 0)
                {
                    if (!FavoriteServers.TryParseAddress(server, out _, out _)) throw new InvalidDataException(T("Проверь адрес сервера: host или host:port.", "Check the server address: host or host:port."));
                    link = link with { Server = server };
                }
                Platform.CopyText(link.WebUri);
                Notice(T("Ссылка скопирована: ", "Link copied: ") + link.WebUri);
                return Task.CompletedTask;
            },
            new FieldModel("source", T("Страница Modrinth или ссылка на .mrpack", "Modrinth page or .mrpack link")),
            new FieldModel("server", T("Сервер (необязательно)", "Server (optional)")));
    }
}
