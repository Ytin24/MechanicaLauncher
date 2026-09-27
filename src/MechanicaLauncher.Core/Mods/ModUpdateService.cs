using System.Text.Json;
using MechanicaLauncher.Core.Instances;
using MechanicaLauncher.Core.Models;
using MechanicaLauncher.Core.Servers;

namespace MechanicaLauncher.Core.Mods;

public sealed partial class ModUpdateService
{
    private static readonly HttpClient DefaultHttp = new();
    private readonly ModrinthClient client;
    private readonly HttpClient http;

    public ModUpdateService(ModrinthClient? client = null, HttpClient? http = null)
        => (this.client, this.http) = (client ?? new ModrinthClient(), http ?? DefaultHttp);

    public async Task<ModUpdateScan> CheckAsync(GameInstance instance, string gameDir, CancellationToken cancellationToken = default)
    {
        var directory = SafePath(gameDir);
        if (!Enum.IsDefined(instance.Loader) || instance.Loader == LoaderType.None || string.IsNullOrWhiteSpace(instance.McVersion))
            throw Error("incompatible", "У сборки не выбрана версия Minecraft и загрузчик модов.");
        string loader = instance.Loader.ToString().ToLowerInvariant();
        List<ModUpdateLocal> files;
        using (ServerModSync.AcquireInstanceLock(directory))
        {
            EnsureNoJournal(directory);
            var managed = await ServerModSync.GetManagedFilesAsync(directory, cancellationToken);
            files = await ReadLocalAsync(directory, cancellationToken);
            files = files.Select(f => f with { Managed = IsManaged(f, managed) }).ToList();
        }
        var errors = new List<string>();
        Dictionary<string, ModrinthVersion>? current = null, updates = null;
        var hashes = files.Where(f => f.Sha1.Length > 0).Select(f => f.Sha1).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (hashes.Length > 0)
        {
            try { current = await client.GetVersionsFromHashesAsync(hashes, cancellationToken); }
            catch (Exception ex) when (NetworkFailure(ex, cancellationToken)) { errors.Add("Не удалось определить установленные версии в Modrinth."); }
        }
        current ??= new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < files.Count; i++)
        {
            var file = files[i];
            if (file.Sha1.Length == 0) { files[i] = file with { State = ModUpdateState.Unavailable }; continue; }
            current.TryGetValue(file.Sha1, out var version);
            bool matched = version != null && !string.IsNullOrWhiteSpace(version.ProjectId) && !string.IsNullOrWhiteSpace(version.Id) &&
                version.Files != null && version.Files.Any(f => f != null && f.Size == file.Size && Same(f.Hashes?.GetValueOrDefault("sha1"), file.Sha1) &&
                    Same(f.Hashes?.GetValueOrDefault("sha512"), file.Sha512));
            files[i] = file with
            {
                Current = matched ? version : null,
                State = file.Managed ? ModUpdateState.Managed : errors.Count > 0 || version != null && !matched ? ModUpdateState.Unavailable :
                    matched ? ModUpdateState.Current : ModUpdateState.Unknown,
                Reason = file.Managed ? "Обновляется вместе с серверной сборкой." : errors.Count > 0 ? errors[0] :
                    version != null && !matched ? "Каталог вернул данные другого файла." : version == null ? "Файл не найден в Modrinth." : null
            };
        }
        var eligible = files.Where(f => !f.Managed && f.Current != null).Select(f => f.Sha1).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (eligible.Length > 0)
        {
            try { updates = await client.GetUpdatesFromHashesAsync(eligible, instance.McVersion, loader, cancellationToken); }
            catch (Exception ex) when (NetworkFailure(ex, cancellationToken)) { errors.Add("Не удалось проверить обновления Modrinth."); }
            for (int i = 0; i < files.Count; i++)
            {
                var file = files[i];
                if (file.Managed || file.Current == null) continue;
                if (updates == null)
                {
                    files[i] = file with { State = ModUpdateState.Unavailable, Reason = errors[^1] };
                    continue;
                }
                if (file.Current.DatePublished == null)
                {
                    files[i] = file with { State = ModUpdateState.Unavailable, Reason = "Неизвестна дата установленной версии." };
                    continue;
                }
                if (!updates.TryGetValue(file.Sha1, out var candidate)) continue;
                try
                {
                    ValidateVersion(candidate, instance.McVersion, loader);
                    if (candidate.ProjectId != file.Current.ProjectId) throw Error("incompatible", "Обновление относится к другому проекту.");
                    if (candidate.Id == file.Current.Id || candidate.DatePublished <= file.Current.DatePublished) continue;
                    files[i] = file with { Target = candidate, State = ModUpdateState.Available, Reason = null };
                }
                catch (ModUpdateException ex) { files[i] = file with { State = ModUpdateState.Incompatible, Reason = ex.Message }; }
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new ModUpdateScan
        {
            InstanceId = instance.Id, Instance = instance, GameDir = directory, Minecraft = instance.McVersion,
            Loader = loader, LoaderVersion = instance.LoaderVersion, Files = files.AsReadOnly(), Errors = errors.AsReadOnly(),
            Items = files.Select(f => new ModUpdateItem(f.FilePath, f.FileName, f.Enabled, Name(f.Current, f.FileName),
                f.Current?.VersionNumber ?? "", f.Target?.VersionNumber, f.State, f.Reason)).ToArray()
        };
    }

    public async Task<ModUpdatePlan> PlanAsync(ModUpdateScan scan, IEnumerable<string> selectedFilePaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scan);
        var selected = selectedFilePaths.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selected.Count == 0) throw Error("conflict", "Выберите моды для обновления.");
        var roots = scan.Files.Where(f => selected.Contains(f.FilePath)).ToArray();
        if (roots.Length != selected.Count || roots.Any(f => f.State != ModUpdateState.Available || f.Target == null || f.Managed))
            throw Error("conflict", "Список выбранных обновлений изменился.");
        using var instanceLock = ServerModSync.AcquireInstanceLock(scan.GameDir);
        EnsureNoJournal(scan.GameDir);
        ValidateInstance(scan);
        await ValidateSnapshotAsync(scan, cancellationToken);
        var managed = await ServerModSync.GetManagedFilesAsync(scan.GameDir, cancellationToken);
        var conflicts = new List<ModUpdateConflict>();
        var resolved = new Dictionary<string, (ModrinthVersion Version, bool Enabled)>(StringComparer.Ordinal);
        var installed = scan.Files.Where(f => f.Current?.ProjectId != null).GroupBy(f => f.Current!.ProjectId!)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var chosen = roots.GroupBy(f => f.Target!.ProjectId!).ToDictionary(g => g.Key, g => g.First().Target!, StringComparer.Ordinal);
        foreach (var root in roots) await ResolveAsync(root.Target!, root.Enabled);
        var effective = scan.Files.Where(f => f.Enabled && f.Current != null && !resolved.ContainsKey(f.Current.ProjectId!))
            .Select(f => (Version: f.Current!, Name: f.FileName)).Concat(resolved.Values.Where(v => v.Enabled)
                .Select(v => (v.Version, Name: Name(v.Version, v.Version.ProjectId!)))).ToArray();
        foreach (var entry in resolved.Values.Where(v => v.Enabled))
            foreach (var dependency in entry.Version.Dependencies.Where(d => d.DependencyType == "incompatible"))
                foreach (var incompatible in effective.Where(f => !string.IsNullOrWhiteSpace(dependency.VersionId) ?
                    f.Version.Id == dependency.VersionId : !string.IsNullOrWhiteSpace(dependency.ProjectId) && f.Version.ProjectId == dependency.ProjectId))
                    conflicts.Add(new(incompatible.Name, "Мод несовместим с выбранным обновлением."));

        var changes = new List<ModUpdateFileChange>();
        foreach (var (project, entry) in resolved)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var version = entry.Version;
            var artifact = ModInstaller.SelectFile(version, ".jar");
            installed.TryGetValue(project, out var beforeFiles);
            if (beforeFiles?.Length > 1)
            {
                conflicts.Add(new(project, "Установлено несколько файлов одного проекта."));
                continue;
            }
            var before = beforeFiles?.SingleOrDefault() ?? scan.Files.FirstOrDefault(f => Same(f.Sha512, artifact.Hashes.GetValueOrDefault("sha512")));
            if (before != null)
            {
                if (entry.Enabled && !before.Enabled)
                {
                    conflicts.Add(new(before.FileName, "Необходимая зависимость отключена."));
                    continue;
                }
                if (before.Current?.Id == version.Id || Same(before.Sha512, artifact.Hashes.GetValueOrDefault("sha512"))) continue;
                if (before.Managed || IsManaged(before, managed))
                {
                    conflicts.Add(new(before.FileName, "Этот файл обновляется вместе с серверной сборкой."));
                    continue;
                }
                if (before.Current?.DatePublished == null || version.DatePublished <= before.Current.DatePublished)
                {
                    conflicts.Add(new(before.FileName, "Зависимость требует другую, не более новую версию."));
                    continue;
                }
            }
            bool enabled = before?.Enabled ?? entry.Enabled;
            string name = before?.FileName ?? artifact.Filename + (enabled ? "" : ".disabled");
            if (before == null && scan.Files.Any(f => f.FileName.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                conflicts.Add(new(name, "Имя занято другим установленным файлом."));
                continue;
            }
            if (managed.Any(f => f.FileName.Equals(BaseFileName(name), StringComparison.OrdinalIgnoreCase)))
            {
                conflicts.Add(new(name, "Имя принадлежит серверной сборке."));
                continue;
            }
            changes.Add(new(name, before, artifact, version, !roots.Any(f => f.Current!.ProjectId == project), enabled));
        }
        foreach (var duplicates in changes.GroupBy(c => c.FileName, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            conflicts.Add(new(duplicates.Key, "Несколько обновлений используют одно имя файла."));
        return new ModUpdatePlan
        {
            Scan = scan, Files = changes.AsReadOnly(), Conflicts = conflicts.Distinct().ToArray(),
            Changes = changes.Select(c => new ModUpdateChange(c.FileName, Name(c.Version, c.FileName),
                c.Before?.Current?.VersionNumber ?? "", c.Version.VersionNumber, c.Dependency, c.Enabled, c.File.Size)).ToArray()
        };

        async Task ResolveAsync(ModrinthVersion version, bool enabled)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateVersion(version, scan.Minecraft, scan.Loader);
            string project = version.ProjectId!;
            if (resolved.TryGetValue(project, out var existing))
            {
                if (existing.Version.Id != version.Id) conflicts.Add(new(project, "Зависимости требуют разные версии одного проекта."));
                if (existing.Version.Id != version.Id || existing.Enabled || !enabled) return;
            }
            if (resolved.Count >= 2048) throw Error("conflict", "Слишком много зависимостей обновления.");
            resolved[project] = (version, enabled);
            foreach (var dependency in version.Dependencies)
            {
                if (dependency.DependencyType != "required") continue;
                ModrinthVersion? needed;
                try
                {
                    if (!string.IsNullOrWhiteSpace(dependency.VersionId)) needed = await client.GetVersionAsync(dependency.VersionId, cancellationToken);
                    else if (!string.IsNullOrWhiteSpace(dependency.ProjectId))
                    {
                        if (!chosen.TryGetValue(dependency.ProjectId, out needed))
                            needed = (await client.GetProjectVersionsAsync(dependency.ProjectId, scan.Minecraft, scan.Loader, cancellationToken))
                                .Where(v => IsCompatibleRelease(v, scan.Minecraft, scan.Loader)).OrderByDescending(v => v.DatePublished).FirstOrDefault();
                    }
                    else throw Error("conflict", "Зависимость не содержит точного проекта или версии.");
                }
                catch (Exception ex) when (NetworkFailure(ex, cancellationToken))
                { throw Error("network_error", "Не удалось проверить зависимости обновления.", ex); }
                if (needed == null || dependency.ProjectId != null && needed.ProjectId != dependency.ProjectId ||
                    dependency.VersionId != null && needed.Id != dependency.VersionId)
                {
                    conflicts.Add(new(Name(version, project), "Требуемая зависимость не найдена."));
                    continue;
                }
                try { await ResolveAsync(needed, enabled); }
                catch (ModUpdateException ex) when (ex.Code == "incompatible") { conflicts.Add(new(Name(needed, project), ex.Message)); }
            }
        }
    }

    private static bool NetworkFailure(Exception ex, CancellationToken ct) => !ct.IsCancellationRequested &&
        ex is HttpRequestException or JsonException or TaskCanceledException;
    private static string Name(ModrinthVersion? version, string fallback) => string.IsNullOrWhiteSpace(version?.Name) ? fallback : version.Name;
    private static bool Same(string? a, string? b) => !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static string BaseFileName(string name) => name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? name[..^9] : name;
    private static bool IsManaged(ModUpdateLocal file, IReadOnlyList<ServerSyncManagedFile> managed) => managed.Any(m =>
        m.FileName.Equals(BaseFileName(file.FileName), StringComparison.OrdinalIgnoreCase) || Same(m.Sha512, file.Sha512));
    private static bool IsCompatibleRelease(ModrinthVersion version, string minecraft, string loader) =>
        version != null && version.VersionType == "release" && version.DatePublished != null &&
        version.GameVersions?.Contains(minecraft) == true && version.Loaders?.Contains(loader) == true;

    private static void ValidateVersion(ModrinthVersion version, string minecraft, string loader)
    {
        if (version == null || string.IsNullOrWhiteSpace(version.Id) || string.IsNullOrWhiteSpace(version.ProjectId) ||
            !IsCompatibleRelease(version, minecraft, loader) || version.Files == null || version.Files.Any(f => f == null || string.IsNullOrWhiteSpace(f.Filename)) ||
            version.Dependencies == null || version.Dependencies.Any(d => d == null))
            throw Error("incompatible", "Версия не является совместимым стабильным релизом.");
        ModrinthFile file;
        try { file = ModInstaller.SelectFile(version, ".jar"); }
        catch (InvalidDataException ex) { throw Error("incompatible", "У версии нет файла мода.", ex); }
        FileName(file.Filename);
        if (file.Size <= 0 || !ValidHash(file.Hashes?.GetValueOrDefault("sha1"), 40) || !ValidHash(file.Hashes?.GetValueOrDefault("sha512"), 128) ||
            !Uri.TryCreate(file.Url, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(url.UserInfo))
            throw Error("incompatible", "У файла отсутствует размер, хеш или безопасный адрес загрузки.");
    }
    private static bool ValidHash(string? value, int length) => value?.Length == length && value.All(Uri.IsHexDigit);
    private static ModUpdateException Error(string code, string message, Exception? inner = null) => new(code, message, inner);
}
