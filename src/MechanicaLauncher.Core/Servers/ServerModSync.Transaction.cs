using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using MechanicaLauncher.Core.Mods;

namespace MechanicaLauncher.Core.Servers;

public sealed partial class ServerModSync
{
    public Task<ServerSyncStage> StageAsync(ServerSyncPlan plan, string stagingDirectory, CancellationToken cancellationToken = default) =>
        Task.Run(() => StageCoreAsync(plan, stagingDirectory, cancellationToken), cancellationToken);

    private async Task<ServerSyncStage> StageCoreAsync(ServerSyncPlan plan, string stagingDirectory, CancellationToken ct)
    {
        using var gate = Acquire(plan.GameDir);
        await CheckPlanAsync(plan, ct);
        await CheckSnapshotAsync(plan, ct);
        string directory = SafePath(Path.Combine(stagingDirectory, "server-sync-" + plan.PlanId.ToString("N")));
        if (Within(directory, SafePath(plan.GameDir))) throw Error("invalid_request", "Загрузка должна находиться вне папки игры.");
        Directory.CreateDirectory(directory);
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var change in plan.Changes.Where(c => c.Kind != ServerSyncChangeKind.Remove))
        {
            ct.ThrowIfCancellationRequested();
            var artifact = plan.Artifacts.Single(a => a.ArtifactId == change.ArtifactId);
            CheckedUrl(artifact.Url.AbsoluteUri, plan.AllowLocalSource);
            if (artifact.External && !externalOrigins.Contains(Origin(artifact.Url))) throw Error("conflict", "Внешний источник больше не разрешён.");
            string path = SafePath(Path.Combine(directory, artifact.FileName));
            if (!await MatchesAsync(path, artifact.Sha512, artifact.Size, ct))
            {
                if (File.Exists(path)) throw Error("hash_mismatch", "Файл загрузки изменён. Создайте новый план.");
                string temporary = SafePath(path + "." + Guid.NewGuid().ToString("N") + ".part");
                try
                {
                    await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                    {
                        await DownloadAsync(artifact.Url, plan.AllowLocalSource, output, artifact.Size, false, ct);
                        output.Flush(flushToDisk: true);
                    }
                    if (!await MatchesAsync(temporary, artifact.Sha512, artifact.Size, ct)) throw Error("hash_mismatch", $"SHA-512 файла не совпадает: {artifact.FileName}");
                    ct.ThrowIfCancellationRequested();
                    SafePath(path);
                    File.Move(temporary, path);
                }
                finally { DeleteTemporary(temporary); }
            }
            string[] ids;
            try { ids = ReadModIds(path); }
            catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException or InvalidOperationException)
            { throw Error("invalid_manifest", $"Не удалось прочитать мод: {artifact.FileName}", ex); }
            if (ids.Length > 0 && artifact.ModIds.Any(id => !ids.Contains(id, StringComparer.Ordinal)))
                throw Error("invalid_manifest", $"Mod ID не соответствует файлу: {artifact.FileName}");
            paths.Add(artifact.ArtifactId, path);
        }
        var stage = new ServerSyncStage(plan, directory, new ReadOnlyDictionary<string, string>(paths));
        await ValidateStagedSetAsync(stage, ct);
        await CheckPlanAsync(plan, ct);
        ct.ThrowIfCancellationRequested();
        return stage;
    }

    private static async Task CheckPlanAsync(ServerSyncPlan plan, CancellationToken ct)
    {
        if (plan.ExpiresUtc <= DateTimeOffset.UtcNow) throw Error("request_expired", "План установки устарел.");
        if (plan.Conflicts.Count > 0) throw Error("conflict", "Сначала нужно разрешить конфликты модов.");
        if (File.Exists(MetaPath(plan.GameDir, "server-sync-journal.json")) || File.Exists(MetaPath(plan.GameDir, "mod-update-journal.json")))
            throw Error("busy", "Сначала нужно восстановить предыдущую установку.");
        var bytes = await ReadOptionalAsync(MetaPath(plan.GameDir, "server-sync.json"), ct);
        if ((bytes == null ? null : Hash(bytes)) != plan.StateHash) throw Error("conflict", "Состав сборки уже обновлён. Создайте новый план.");
        var seen = (await ReadSeenAsync(plan.GameDir, ct)).SingleOrDefault(s => s.ServerId == plan.ServerId && s.Origin == Origin(plan.DescriptorUrl) && s.Target.TargetId == plan.Target.TargetId);
        if (seen == null || seen.Revision != plan.Revision || seen.ManifestSha512 != plan.ManifestSha512 || seen.Target != plan.Target)
            throw Error("request_expired", "Доступна новая ревизия сервера. Создайте новый план.");
    }

    private static async Task CheckSnapshotAsync(ServerSyncPlan plan, CancellationToken ct)
    {
        var files = await ReadLocalAsync(plan.GameDir, ct);
        if (files.Count != plan.OriginalFiles.Count || files.Any(f => !plan.OriginalFiles.Any(o =>
            o.FileName == f.FileName && o.Size == f.Size && o.Sha512 == f.Sha512 && o.Enabled == f.Enabled)))
            throw Error("conflict", "Локальные моды изменились после проверки.");
    }

    private static async Task ValidateStagedSetAsync(ServerSyncStage stage, CancellationToken ct)
    {
        var plan = stage.Plan;
        string validation = SafePath(Path.Combine(stage.DirectoryPath, "check-" + Guid.NewGuid().ToString("N")));
        string mods = Path.Combine(validation, "mods");
        Directory.CreateDirectory(mods);
        var copied = new List<string>();
        try
        {
            foreach (var local in plan.OriginalFiles.Where(f => f.Enabled && !plan.RemoveFiles.Contains(f.FileName, StringComparer.OrdinalIgnoreCase)))
            {
                string destination = SafePath(Path.Combine(mods, local.FileName));
                copied.Add(destination);
                await CopyCheckedAsync(ModPath(plan.GameDir, local.FileName), destination, local.Sha512, local.Size, ct);
            }
            foreach (var (id, path) in stage.Files)
            {
                var artifact = plan.Artifacts.Single(a => a.ArtifactId == id);
                string destination = SafePath(Path.Combine(mods, artifact.FileName));
                copied.Add(destination);
                await CopyCheckedAsync(path, destination, artifact.Sha512, artifact.Size, ct);
            }
            await CheckCompatibilityAsync(plan, validation, ct);
        }
        finally
        {
            foreach (string path in copied) DeleteTemporary(path);
            try { SafePath(mods); Directory.Delete(mods); SafePath(validation); Directory.Delete(validation); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ServerModSyncException) { }
        }
    }

    private static async Task CheckCompatibilityAsync(ServerSyncPlan plan, string directory, CancellationToken ct)
    {
        var report = await new ModCompatibilityChecker().CheckAsync(plan.Instance, directory, false, ct);
        var issues = report.Issues.Where(i => i.IsError || i.Code == "dependency").ToArray();
        if (issues.Length > 0) throw Error("conflict", "Несовместимый состав модов: " + string.Join("; ", issues.Take(6).Select(i => i.Detail)));
    }

    public Task ApplyAsync(ServerSyncStage stage, Func<bool> isGameRunning, CancellationToken cancellationToken = default) =>
        Task.Run(() => ApplyCoreAsync(stage, isGameRunning, cancellationToken), cancellationToken);

    private static async Task ApplyCoreAsync(ServerSyncStage stage, Func<bool> isGameRunning, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(isGameRunning);
        var plan = stage.Plan;
        using var gate = Acquire(plan.GameDir);
        string journalPath = MetaPath(plan.GameDir, "server-sync-journal.json");
        string statePath = MetaPath(plan.GameDir, "server-sync.json");
        if (File.Exists(journalPath) || File.Exists(MetaPath(plan.GameDir, "mod-update-journal.json")))
            throw Error("busy", "Сначала нужно восстановить предыдущую установку.");
        var previousBytes = await ReadOptionalAsync(statePath, ct);
        var state = ReadState(previousBytes);
        if (state.Servers.Any(s => s.LastPlanId == plan.PlanId)) return;
        await CheckPlanAsync(plan, ct);
        if (isGameRunning()) throw Error("busy", "Сначала завершите игру этой сборки.");
        await CheckSnapshotAsync(plan, ct);
        if (Within(SafePath(stage.DirectoryPath), SafePath(plan.GameDir))) throw Error("invalid_request", "Недопустимая папка загрузки.");
        var changes = plan.Changes.Where(c => c.Kind != ServerSyncChangeKind.Remove).ToArray();
        if (stage.Files.Count != changes.Length) throw Error("invalid_request", "Проверенная загрузка неполна.");
        foreach (var change in changes)
        {
            var artifact = plan.Artifacts.Single(a => a.ArtifactId == change.ArtifactId);
            if (!stage.Files.TryGetValue(change.ArtifactId, out var path) || !Within(SafePath(path), SafePath(stage.DirectoryPath)) ||
                !await MatchesAsync(path, artifact.Sha512, artifact.Size, ct))
                throw Error("hash_mismatch", "Проверенная загрузка изменилась.");
        }
        var entries = new List<ServerSyncJournalEntry>();
        var names = plan.RemoveFiles.Concat(changes.Select(c => c.FileName)).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            var before = plan.OriginalFiles.SingleOrDefault(f => f.FileName.Equals(name, StringComparison.OrdinalIgnoreCase));
            var change = changes.SingleOrDefault(c => c.FileName.Equals(name, StringComparison.OrdinalIgnoreCase));
            var after = change == null ? null : plan.Artifacts.Single(a => a.ArtifactId == change.ArtifactId);
            entries.Add(new(name, before?.Sha512, before?.Size ?? 0, after?.Sha512, after?.Size ?? 0));
        }
        var journal = new ServerSyncJournal
        {
            PlanId = plan.PlanId, PreviousState = previousBytes == null ? null : Encoding.UTF8.GetString(previousBytes),
            NextStateHash = Hash(JsonSerializer.SerializeToUtf8Bytes(plan.NextState, JsonOptions)), Entries = entries
        };
        string backup = BackupDirectory(plan.GameDir, journal.PlanId);
        if (Directory.Exists(backup) || File.Exists(backup)) throw Error("conflict", "Папка резервной копии уже существует.");
        Directory.CreateDirectory(backup);
        bool published = false, committed = false;
        try
        {
            foreach (var entry in entries.Where(e => e.BeforeHash != null))
                await CopyCheckedAsync(ModPath(plan.GameDir, entry.FileName), SafePath(Path.Combine(backup, entry.FileName + ".bak")), entry.BeforeHash!, entry.BeforeSize, ct);
            await CheckSnapshotAsync(plan, ct);
            ct.ThrowIfCancellationRequested();
            if (isGameRunning()) throw Error("busy", "Игра запущена. Изменения не применены.");
            await WriteJsonAsync(journalPath, journal, ct);
            published = true;
            Directory.CreateDirectory(SafePath(Path.Combine(plan.GameDir, "mods")));
            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                if (isGameRunning()) throw Error("busy", "Игра запущена во время установки.");
                string destination = ModPath(plan.GameDir, entry.FileName);
                await RequireBeforeAsync(destination, entry, ct);
                if (entry.AfterHash == null) File.Delete(destination);
                else
                {
                    var artifact = plan.Artifacts.Single(a => a.FileName.Equals(entry.FileName, StringComparison.OrdinalIgnoreCase));
                    string temporary = SafePath(destination + "." + plan.PlanId.ToString("N") + ".tmp");
                    try
                    {
                        await CopyCheckedAsync(stage.Files[artifact.ArtifactId], temporary, entry.AfterHash, entry.AfterSize, ct);
                        await RequireBeforeAsync(destination, entry, ct);
                        ct.ThrowIfCancellationRequested();
                        SafePath(destination);
                        File.Move(temporary, destination, overwrite: entry.BeforeHash != null);
                    }
                    finally { DeleteTemporary(temporary); }
                }
            }
            await CheckCompatibilityAsync(plan, plan.GameDir, ct);
            ct.ThrowIfCancellationRequested();
            if (isGameRunning()) throw Error("busy", "Игра запущена во время установки.");
            await WriteJsonAsync(statePath, plan.NextState, ct);
            journal.Committed = true;
            await WriteJsonAsync(journalPath, journal, ct);
            committed = true;
        }
        catch (Exception original)
        {
            if (published && !committed)
            {
                try { await RollbackAsync(plan.GameDir, journal, CancellationToken.None); }
                catch (Exception rollback) { throw Error("conflict", "Откат не завершён. Сохранён журнал восстановления.", new AggregateException(original, rollback)); }
            }
            throw;
        }
        finally
        {
            if (!published || committed)
            {
                try { CleanupJournal(plan.GameDir, journal, published); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ServerModSyncException) { }
            }
        }
    }

    private static async Task RequireBeforeAsync(string destination, ServerSyncJournalEntry entry, CancellationToken ct)
    {
        SafePath(destination);
        if (Directory.Exists(destination) || (entry.BeforeHash == null ? File.Exists(destination) : !await MatchesAsync(destination, entry.BeforeHash, entry.BeforeSize, ct)))
            throw Error("conflict", "Файл изменился во время установки: " + entry.FileName);
    }

    private static string BackupDirectory(string gameDir, Guid planId) => MetaPath(gameDir, Path.Combine("server-sync-backups", planId.ToString("N")));

    public Task RecoverAsync(string gameDir, CancellationToken cancellationToken = default) =>
        Task.Run(() => RecoverCoreAsync(gameDir, cancellationToken), cancellationToken);

    private static async Task RecoverCoreAsync(string gameDir, CancellationToken ct)
    {
        string directory = SafePath(gameDir);
        if (!File.Exists(MetaPath(directory, "server-sync-journal.json"))) return;
        using var gate = Acquire(directory);
        if (File.Exists(MetaPath(directory, "mod-update-journal.json"))) throw Error("conflict", "Найдены две незавершённые установки.");
        var bytes = await ReadOptionalAsync(MetaPath(directory, "server-sync-journal.json"), ct);
        if (bytes == null) return;
        var journal = ReadJson<ServerSyncJournal>(bytes, MaxStateBytes);
        ValidateJournal(journal);
        if (journal.Committed)
        {
            var state = await ReadOptionalAsync(MetaPath(directory, "server-sync.json"), ct);
            if (state == null || Hash(state) != journal.NextStateHash) throw Error("conflict", "Не совпадает реестр завершённой установки.");
            CleanupJournal(directory, journal, true);
        }
        else
        {
            ct.ThrowIfCancellationRequested();
            await RollbackAsync(directory, journal, CancellationToken.None);
        }
    }

    private static void ValidateJournal(ServerSyncJournal journal)
    {
        if (journal.Version != 1 || journal.PlanId == Guid.Empty || !ValidHash(journal.NextStateHash) || journal.Entries == null || journal.Entries.Count > MaxFiles * 2 ||
            journal.Entries.Any(e => e == null || FileName(e.FileName) != e.FileName || e.BeforeHash == null && e.AfterHash == null ||
                e.BeforeHash != null && (!ValidHash(e.BeforeHash) || e.BeforeSize is <= 0 or > MaxFileBytes) ||
                e.AfterHash != null && (!ValidHash(e.AfterHash) || e.AfterSize is <= 0 or > MaxFileBytes)) ||
            journal.Entries.Select(e => e.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != journal.Entries.Count)
            throw Error("conflict", "Повреждён журнал установки.");
        if (journal.PreviousState != null) ReadState(Encoding.UTF8.GetBytes(journal.PreviousState));
    }

    private static async Task RollbackAsync(string gameDir, ServerSyncJournal journal, CancellationToken ct)
    {
        ValidateJournal(journal);
        string statePath = MetaPath(gameDir, "server-sync.json");
        var state = await ReadOptionalAsync(statePath, ct);
        byte[]? previous = journal.PreviousState == null ? null : Encoding.UTF8.GetBytes(journal.PreviousState);
        if ((state == null ? null : Hash(state)) != (previous == null ? null : Hash(previous)) && (state == null || Hash(state) != journal.NextStateHash))
            throw Error("conflict", "Реестр изменён после прерванной установки.");
        foreach (var entry in journal.Entries.AsEnumerable().Reverse())
        {
            string path = ModPath(gameDir, entry.FileName);
            if (entry.BeforeHash != null && await MatchesAsync(path, entry.BeforeHash, entry.BeforeSize, ct)) continue;
            bool exists = File.Exists(path);
            if (Directory.Exists(path) || exists && (entry.AfterHash == null || !await MatchesAsync(path, entry.AfterHash, entry.AfterSize, ct)))
                throw Error("conflict", "Файл изменён после прерванной установки: " + entry.FileName);
            if (entry.BeforeHash == null)
            {
                if (exists) File.Delete(path);
                continue;
            }
            string backup = SafePath(Path.Combine(BackupDirectory(gameDir, journal.PlanId), entry.FileName + ".bak"));
            if (!await MatchesAsync(backup, entry.BeforeHash, entry.BeforeSize, ct)) throw Error("conflict", "Резервная копия повреждена: " + entry.FileName);
            string temporary = SafePath(path + "." + Guid.NewGuid().ToString("N") + ".rollback");
            try
            {
                await CopyCheckedAsync(backup, temporary, entry.BeforeHash, entry.BeforeSize, ct);
                SafePath(path);
                File.Move(temporary, path, overwrite: exists);
            }
            finally { DeleteTemporary(temporary); }
        }
        if (previous == null) { SafePath(statePath); File.Delete(statePath); }
        else await WriteBytesAsync(statePath, previous, ct);
        CleanupJournal(gameDir, journal, true);
    }

    private static void CleanupJournal(string gameDir, ServerSyncJournal journal, bool deleteJournal)
    {
        string directory = BackupDirectory(gameDir, journal.PlanId);
        if (Directory.Exists(directory))
        {
            foreach (var entry in journal.Entries.Where(e => e.BeforeHash != null))
                File.Delete(SafePath(Path.Combine(directory, FileName(entry.FileName) + ".bak")));
            SafePath(directory);
            Directory.Delete(directory);
        }
        if (deleteJournal) File.Delete(MetaPath(gameDir, "server-sync-journal.json"));
    }
}
