using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MechanicaLauncher.Core.Instances;
using MechanicaLauncher.Core.Models;
using MechanicaLauncher.Core.Mods;

namespace MechanicaLauncher.Core.Servers;

public sealed partial class ServerModSync
{
    private readonly HttpClient http;
    private readonly ModrinthClient client;
    private readonly HashSet<string> externalOrigins;

    public ServerModSync(HttpClient? http = null, ModrinthClient? client = null, IEnumerable<Uri>? approvedExternalOrigins = null)
    {
        this.http = http ?? DefaultHttp;
        this.client = client ?? new ModrinthClient(new HttpClient(new CatalogHandler(this)) { BaseAddress = new("https://api.modrinth.com") });
        externalOrigins = (approvedExternalOrigins ?? []).Select(u => Origin(CheckedUrl(u.AbsoluteUri, true))).ToHashSet(StringComparer.Ordinal);
    }

    public Task<ServerSyncPlan> PlanAsync(Uri descriptorUrl, GameInstance instance, string gameDir,
        bool allowLocalSource = false, CancellationToken cancellationToken = default) =>
        Task.Run(() => PlanCoreAsync(descriptorUrl, instance, gameDir, allowLocalSource, cancellationToken), cancellationToken);

    private async Task<ServerSyncPlan> PlanCoreAsync(Uri descriptorUrl, GameInstance instance, string gameDir, bool allowLocal, CancellationToken ct)
    {
        instance = new() { Id = instance.Id, McVersion = instance.McVersion, Loader = instance.Loader, LoaderVersion = instance.LoaderVersion, JavaPath = instance.JavaPath };
        string directory = SafePath(gameDir);
        var endpoint = CheckedUrl(descriptorUrl.AbsoluteUri, allowLocal);
        string? loader = instance.Loader == LoaderType.None ? null : instance.Loader.ToString().ToLowerInvariant();
        if (!Enum.IsDefined(instance.Loader) || string.IsNullOrWhiteSpace(instance.McVersion) || loader != null && string.IsNullOrWhiteSpace(instance.LoaderVersion))
            throw Error("target_mismatch", "Версия выбранной сборки не определена.");
        var descriptor = ReadJson<Descriptor>(await ReadNetworkBytesAsync(endpoint, allowLocal, ct));
        if (descriptor.DescriptorVersion != 1 || descriptor.Protocols == null || descriptor.Protocols.Length == 0)
            throw Error("unsupported_protocol", "Версия протокола сервера не поддерживается.");
        if (!Guid.TryParse(descriptor.ServerId, out var serverId) || serverId == Guid.Empty || descriptor.Protocols.Any(p => p == null || p.Major is < 1 or > 65535 || p.Minor is < 0 or > 65535))
            throw Error("invalid_manifest", "Некорректное описание сервера.");
        var protocols = descriptor.Protocols.Where(p => p.Major == 1 && p.Minor == 0).ToArray();
        if (protocols.Length != 1 || protocols[0].RequiredCapabilities == null ||
            !protocols[0].RequiredCapabilities.Contains("mods-v1") || protocols[0].RequiredCapabilities.Any(c => c != "mods-v1"))
            throw Error("unsupported_protocol", "Требования протокола сервера не поддерживаются.");
        var targets = protocols[0].Targets;
        if (targets == null || targets.Length > MaxFiles || targets.Any(t => t == null || !ValidTarget(t.TargetId, t.Minecraft, t.Loader, t.LoaderVersion)) ||
            targets.Select(t => t.TargetId).Distinct(StringComparer.Ordinal).Count() != targets.Length)
            throw Error("invalid_manifest", "Некорректные версии сборок сервера.");
        var matches = targets.Where(t => t.Minecraft == instance.McVersion && t.Loader == loader && t.LoaderVersion == (loader == null ? null : instance.LoaderVersion)).ToArray();
        if (matches.Length != 1) throw Error("target_mismatch", "На сервере нет состава для этой версии Minecraft и загрузчика.");
        var selected = matches[0];
        if (!ValidHash(selected.Sha512)) throw Error("invalid_manifest", "Отсутствует SHA-512 манифеста.");
        var manifestUrl = CheckedUrl(selected.ManifestUrl, allowLocal);
        if (Origin(endpoint) != Origin(manifestUrl)) throw Error("invalid_manifest", "Манифест расположен на другом источнике.");
        var bytes = await ReadNetworkBytesAsync(manifestUrl, allowLocal, ct);
        string manifestHash = Hash(bytes);
        if (!manifestHash.Equals(selected.Sha512, StringComparison.OrdinalIgnoreCase)) throw Error("hash_mismatch", "SHA-512 манифеста не совпадает.");
        var manifest = ReadJson<Manifest>(bytes);
        var target = new ServerSyncTarget(selected.TargetId, selected.Minecraft, selected.Loader, selected.LoaderVersion);
        if (manifest.Type != "Manifest" || manifest.ProtocolMajor != 1 || manifest.ProtocolMinor != 0 ||
            !Guid.TryParse(manifest.ServerId, out var manifestServer) || manifestServer != serverId || manifest.TargetId != target.TargetId ||
            manifest.Minecraft != target.Minecraft || manifest.Loader != target.Loader || manifest.LoaderVersion != target.LoaderVersion ||
            manifest.Revision is < 1 or > 9007199254740991)
            throw Error("invalid_manifest", "Манифест не соответствует описанию сервера.");
        string? expiry = manifest.ExpiresUtc;
        if (expiry != null && Regex.IsMatch(expiry, @"\.\d{8,9}Z$", RegexOptions.CultureInvariant))
            expiry = expiry[..(expiry.IndexOf('.') + 8)] + "Z";
        if (manifest.ExpiresUtc == null || !Regex.IsMatch(manifest.ExpiresUtc, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,9})?Z$", RegexOptions.CultureInvariant) ||
            !DateTimeOffset.TryParse(expiry, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var expires))
            throw Error("invalid_manifest", "Некорректный срок действия манифеста.");
        if (expires <= DateTimeOffset.UtcNow) throw Error("request_expired", "Манифест сервера устарел.");
        ValidateFiles(manifest.Files);
        var required = manifest.Files.Where(f => f.Client == "required").ToArray();
        if (required.Length != 0 && loader == null) throw Error("target_mismatch", "Для серверных модов нужен загрузчик.");
        var versions = new Dictionary<string, ModrinthVersion>(StringComparer.Ordinal);
        var artifacts = new List<ServerSyncArtifact>();
        foreach (var file in required)
        {
            ct.ThrowIfCancellationRequested();
            Uri url;
            if (file.Source.Type == "external")
            {
                url = CheckedUrl(file.Source.Url, allowLocal);
                if (!externalOrigins.Contains(Origin(url))) throw Error("conflict", $"Внешний источник не разрешён: {Origin(url)}");
            }
            else
            {
                string id = file.Source.VersionId!;
                if (!versions.TryGetValue(id, out var version))
                {
                    try { version = await client.GetVersionAsync(id, ct) ?? throw Error("invalid_manifest", "Версия Modrinth не найдена."); }
                    catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or JsonException or TaskCanceledException)
                    { throw Error("network_error", "Не удалось проверить версию Modrinth.", ex); }
                    versions[id] = version;
                }
                if (version.Id != id || version.ProjectId != file.Source.ProjectId || version.GameVersions == null || !version.GameVersions.Contains(target.Minecraft) ||
                    version.Loaders == null || !version.Loaders.Contains(target.Loader!) || version.Files == null || version.Dependencies == null)
                    throw Error("invalid_manifest", $"Версия Modrinth не соответствует манифесту: {file.Filename}");
                var files = version.Files.Where(f => f != null && f.Filename == file.Filename && f.Size == file.Size && f.Hashes != null &&
                    f.Hashes.TryGetValue("sha512", out var hash) && hash.Equals(file.Sha512, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (files.Length != 1) throw Error("invalid_manifest", $"Файл не подтверждён Modrinth: {file.Filename}");
                url = CheckedUrl(files[0].Url, false);
                if (Origin(url) != "https://cdn.modrinth.com") throw Error("invalid_manifest", "Недопустимый источник файла Modrinth.");
            }
            artifacts.Add(new(file.ArtifactId, file.ModIds, file.Filename, file.Size, file.Sha512.ToLowerInvariant(), url, file.Source.Type == "external", file.Source.ProjectId, file.Source.VersionId));
        }
        CheckCatalogDependencies(required, versions);
        using var gate = Acquire(directory);
        if (File.Exists(MetaPath(directory, "server-sync-journal.json")) || File.Exists(MetaPath(directory, "mod-update-journal.json")))
            throw Error("busy", "Сначала нужно восстановить предыдущую установку.");
        var stateBytes = await ReadOptionalAsync(MetaPath(directory, "server-sync.json"), ct);
        var state = ReadState(stateBytes);
        var previous = state.Servers.SingleOrDefault(s => s.ServerId == serverId && s.Origin == Origin(endpoint) && s.Target.TargetId == target.TargetId);
        if (previous != null) CheckRevision(previous.Target, previous.Revision, previous.ManifestSha512, target, manifest.Revision, manifestHash);
        var seen = await ReadSeenAsync(directory, ct);
        var lastSeen = seen.SingleOrDefault(s => s.ServerId == serverId && s.Origin == Origin(endpoint) && s.Target.TargetId == target.TargetId);
        if (lastSeen != null) CheckRevision(lastSeen.Target, lastSeen.Revision, lastSeen.ManifestSha512, target, manifest.Revision, manifestHash);
        var local = await ReadLocalAsync(directory, ct);
        var conflicts = new List<ServerSyncConflict>();
        var changes = new List<ServerSyncChange>();
        var managed = previous?.Files ?? [];
        var owned = new Dictionary<string, ServerSyncManagedFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in managed)
        {
            var current = local.SingleOrDefault(f => f.FileName.Equals(file.FileName, StringComparison.OrdinalIgnoreCase));
            if (current == null) continue;
            if (current.Sha512 != file.Sha512 || current.Size != file.Size)
                conflicts.Add(new(current.FileName, "Управляемый файл был изменён."));
            else owned.Add(current.FileName, file);
        }
        var keep = new List<ServerSyncManagedFile>();
        var remove = owned.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var replaced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var artifact in artifacts)
        {
            var exact = local.Where(f => f.Sha512 == artifact.Sha512 && f.Size == artifact.Size).ToArray();
            if (exact.Any(f => !f.Enabled)) conflicts.Add(new(exact.First(f => !f.Enabled).FileName, "Нужный мод отключён."));
            var enabled = exact.Where(f => f.Enabled).ToArray();
            if (enabled.Length > 1) conflicts.Add(new(artifact.FileName, "Мод установлен несколько раз."));
            if (enabled.Length > 0)
            {
                var current = enabled[0];
                if (current.ModIds.Length > 0 && artifact.ModIds.Any(id => !current.ModIds.Contains(id, StringComparer.Ordinal)))
                    throw Error("invalid_manifest", $"Mod ID не соответствует файлу: {current.FileName}");
                if (owned.ContainsKey(current.FileName))
                {
                    remove.Remove(current.FileName);
                    keep.Add(new(artifact.ArtifactId, current.FileName, artifact.Size, artifact.Sha512));
                }
                continue;
            }
            var old = owned.Values.SingleOrDefault(f => f.ArtifactId == artifact.ArtifactId);
            changes.Add(new(old == null ? ServerSyncChangeKind.Add : ServerSyncChangeKind.Replace, artifact.ArtifactId, artifact.FileName, artifact.Size));
            if (old != null) replaced.Add(old.ArtifactId);
            keep.Add(new(artifact.ArtifactId, artifact.FileName, artifact.Size, artifact.Sha512));
        }
        foreach (var artifact in artifacts)
        {
            foreach (var current in local.Where(f => !remove.Contains(f.FileName) && f.Sha512 != artifact.Sha512 &&
                (f.FileName.Equals(artifact.FileName, StringComparison.OrdinalIgnoreCase) || f.FileName.Equals(artifact.FileName + ".disabled", StringComparison.OrdinalIgnoreCase) || f.ModIds.Intersect(artifact.ModIds, StringComparer.Ordinal).Any())))
                conflicts.Add(new(current.FileName, "Личный файл конфликтует с серверным модом."));
            if (Directory.Exists(SafePath(Path.Combine(directory, "mods", artifact.FileName))))
                conflicts.Add(new(artifact.FileName, "Вместо файла существует папка."));
        }
        foreach (var name in remove)
        {
            var file = owned[name];
            if (!replaced.Contains(file.ArtifactId)) changes.Add(new(ServerSyncChangeKind.Remove, file.ArtifactId, name, 0));
        }
        if (changes.Count == 0)
        {
            var report = await new ModCompatibilityChecker().CheckAsync(instance, directory, false, ct);
            conflicts.AddRange(report.Issues.Where(i => i.IsError).Select(i => new ServerSyncConflict(i.Detail, "Несовместимый состав модов: " + i.Code)));
        }
        var next = new ServerSyncState { Servers = state.Servers.Where(s => s != previous).ToList() };
        var plan = new ServerSyncPlan
        {
            ServerId = serverId, Target = target, Revision = manifest.Revision, ManifestSha512 = manifestHash,
            DescriptorUrl = endpoint, AllowLocalSource = allowLocal, GameDir = directory,
            ExpiresUtc = expires < DateTimeOffset.UtcNow.AddMinutes(15) ? expires : DateTimeOffset.UtcNow.AddMinutes(15),
            Instance = new() { Id = instance.Id, McVersion = instance.McVersion, Loader = instance.Loader, LoaderVersion = instance.LoaderVersion, JavaPath = instance.JavaPath },
            Changes = changes.AsReadOnly(), Conflicts = conflicts.Distinct().ToArray(), Artifacts = artifacts.AsReadOnly(),
            OriginalFiles = local.AsReadOnly(), StateHash = stateBytes == null ? null : Hash(stateBytes), NextState = next, RemoveFiles = remove.ToArray()
        };
        next.Servers.Add(new(serverId, Origin(endpoint), target, manifest.Revision, manifestHash, plan.PlanId, keep));
        if (next.Servers.Count > 64 || next.Servers.Sum(s => s.Files.Count) > MaxFiles) throw Error("invalid_manifest", "Слишком много управляемых модов.");
        seen.RemoveAll(s => s.ServerId == serverId && s.Origin == Origin(endpoint) && s.Target.TargetId == target.TargetId);
        seen.Add(new(serverId, Origin(endpoint), target, manifest.Revision, manifestHash));
        if (seen.Count > 128) throw Error("invalid_manifest", "Слишком много описаний серверов для сборки.");
        await WriteJsonAsync(MetaPath(directory, "server-sync-seen.json"), seen, ct);
        return plan;
    }

    private static bool ValidTarget(string? id, string? minecraft, string? loader, string? loaderVersion) => ValidId(id) &&
        !string.IsNullOrWhiteSpace(minecraft) && minecraft.Length <= 128 &&
        (loader == null ? loaderVersion == null : loader is "fabric" or "forge" or "neoforge" or "quilt" && !string.IsNullOrWhiteSpace(loaderVersion) && loaderVersion.Length <= 128);

    private static void ValidateFiles(ManifestFile[]? files)
    {
        if (files == null || files.Length > MaxFiles) throw Error("invalid_manifest", "Превышено число файлов манифеста.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var modIds = new HashSet<string>(StringComparer.Ordinal);
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var f in files)
        {
            if (f == null || !ValidId(f.ArtifactId) || !ids.Add(f.ArtifactId) || !names.Add(FileName(f.Filename)) || !ValidHash(f.Sha512) ||
                f.Size is <= 0 or > MaxFileBytes || f.Client is not ("required" or "optional" or "unsupported") ||
                f.ModIds == null || f.ModIds.Length is < 1 or > 256 || f.ModIds.Any(id => !ValidModId(id)) || f.ModIds.Distinct(StringComparer.Ordinal).Count() != f.ModIds.Length ||
                f.Dependencies == null || f.Dependencies.Length > MaxFiles || f.Dependencies.Any(id => !ValidId(id) || id == f.ArtifactId) ||
                f.Dependencies.Distinct(StringComparer.Ordinal).Count() != f.Dependencies.Length || f.Source == null || f.Source.Type is not ("modrinth" or "external"))
                throw Error("invalid_manifest", "Некорректная запись файла манифеста.");
            if (f.Source.Type == "modrinth" && (!CatalogId(f.Source.ProjectId) || !CatalogId(f.Source.VersionId)) || f.Source.Type == "external" && string.IsNullOrWhiteSpace(f.Source.Url))
                throw Error("invalid_manifest", "Не указан точный источник файла.");
            if (f.Client == "required")
            {
                total += f.Size;
                if (total > MaxPlanBytes || !hashes.Add(f.Sha512) || f.ModIds.Any(id => !modIds.Add(id)))
                    throw Error("invalid_manifest", "Повторяющиеся моды или превышение размера сборки.");
            }
        }
        var selected = files.Where(f => f.Client == "required").Select(f => f.ArtifactId).ToHashSet(StringComparer.Ordinal);
        foreach (var file in files)
            if (file.Dependencies.Any(d => !ids.Contains(d) || file.Client == "required" && !selected.Contains(d)))
                throw Error("invalid_manifest", "Обязательные зависимости отсутствуют в составе клиента.");
    }

    private static bool CatalogId(string? id) => id != null && Regex.IsMatch(id, "^[a-zA-Z0-9]{1,64}$", RegexOptions.CultureInvariant);
    private static bool ValidModId(string? id) => id != null && Regex.IsMatch(id, "^[a-zA-Z0-9_][a-zA-Z0-9_.-]{0,127}$", RegexOptions.CultureInvariant);

    private static void CheckCatalogDependencies(ManifestFile[] files, Dictionary<string, ModrinthVersion> versions)
    {
        foreach (var file in files.Where(f => f.Source.Type == "modrinth"))
        {
            foreach (var d in versions[file.Source.VersionId!].Dependencies)
            {
                if (d == null) throw Error("invalid_manifest", "Некорректные зависимости Modrinth.");
                bool Matches(ManifestFile f) => f.Source.Type == "modrinth" && (d.VersionId != null ? f.Source.VersionId == d.VersionId : d.ProjectId != null && f.Source.ProjectId == d.ProjectId);
                if (d.DependencyType == "required" && !files.Any(f => file.Dependencies.Contains(f.ArtifactId) && Matches(f)))
                    throw Error("invalid_manifest", $"Не закреплена обязательная зависимость: {file.Filename}");
                if (d.DependencyType == "incompatible" && files.Any(Matches))
                    throw Error("invalid_manifest", $"Несовместимые версии Modrinth: {file.Filename}");
            }
        }
    }

    private static void CheckRevision(ServerSyncTarget oldTarget, long oldRevision, string oldHash, ServerSyncTarget target, long revision, string hash)
    {
        if (oldTarget != target || revision < oldRevision || revision == oldRevision && hash != oldHash)
            throw Error("invalid_manifest", "Ревизия или состав сервера изменились некорректно.");
    }

    private sealed record Descriptor(int DescriptorVersion, string ServerId, Protocol[] Protocols);
    private sealed record Protocol(int Major, int Minor, string[] RequiredCapabilities, Target[] Targets);
    private sealed record Target(string TargetId, string Minecraft, string? Loader, string? LoaderVersion, string ManifestUrl, string Sha512);
    private sealed record Manifest(string Type, int ProtocolMajor, int ProtocolMinor, string ServerId, string TargetId, long Revision,
        string ExpiresUtc, string Minecraft, string? Loader, string? LoaderVersion, ManifestFile[] Files);
    private sealed record ManifestFile(string ArtifactId, string[] ModIds, string Client, string Filename, long Size, string Sha512, string[] Dependencies, Source Source);
    private sealed record Source(string Type, string? ProjectId, string? VersionId, string? Url);
}
