using System.Security.Cryptography;
using System.Text.Json;
using MechanicaLauncher.Core.IO;
using MechanicaLauncher.Core.Servers;

namespace MechanicaLauncher.Core.Mods;

public sealed partial class ModUpdateService
{
    private static readonly JsonSerializerOptions JournalOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 16 };

    public async Task<ModUpdateResult> ApplyAsync(ModUpdatePlan plan, Func<bool> isRunning, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(isRunning);
        if (isRunning()) throw Error("busy", "Сначала закройте игру.");
        if (plan.Conflicts.Count > 0) throw Error("conflict", "Сначала устраните конфликты обновления.");
        if (!plan.HasChanges) return new(0, "");
        using var instanceLock = ServerModSync.AcquireInstanceLock(plan.GameDir);
        EnsureNoJournal(plan.GameDir);
        ValidateInstance(plan.Scan);
        await ValidateSnapshotAsync(plan.Scan, cancellationToken);
        await ValidateManagedAsync(plan, cancellationToken);
        Guid transactionId = Guid.NewGuid();
        string transaction = TransactionPath(plan.GameDir, transactionId);
        string game = SafePath(Path.Combine(transaction, "prepared"));
        string preparedMods = SafePath(Path.Combine(game, "mods"));
        string backup = SafePath(Path.Combine(transaction, "backup"));
        Directory.CreateDirectory(preparedMods);
        Directory.CreateDirectory(backup);
        try
        {
            foreach (var local in plan.Scan.Files)
                await CopyCheckedAsync(local.FilePath, SafePath(Path.Combine(preparedMods, local.FileName)), local.Sha512, local.Size, cancellationToken);
            foreach (var change in plan.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string target = SafePath(Path.Combine(preparedMods, FileName(change.FileName)));
                await FileDownloader.EnsureAsync(http, change.File.Url, target, change.File.Hashes["sha1"], change.File.Size,
                    cancellationToken, change.File.Hashes["sha512"]);
            }
            string overrides = SafePath(Path.Combine(plan.GameDir, "config", "fabric_loader_dependencies.json"));
            if (File.Exists(overrides))
            {
                string target = SafePath(Path.Combine(game, "config", "fabric_loader_dependencies.json"));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await CopyCheckedAsync(overrides, target, await HashAsync(overrides, cancellationToken), new FileInfo(overrides).Length, cancellationToken);
            }
            var compatibility = await new ModCompatibilityChecker(client).CheckAsync(plan.Scan.Instance, game, false, cancellationToken);
            var issues = compatibility.Issues.Where(i => i.IsError || i.Code == "dependency").ToArray();
            if (issues.Length > 0) throw Error("incompatible", "Обновление несовместимо: " + string.Join("; ", issues.Take(5).Select(i => i.Detail)));
            if (isRunning()) throw Error("busy", "Сначала закройте игру.");
            cancellationToken.ThrowIfCancellationRequested();
            ValidateInstance(plan.Scan);
            await ValidateSnapshotAsync(plan.Scan, cancellationToken);
            await ValidateManagedAsync(plan, cancellationToken);

            var journal = new UpdateJournal
            {
                PlanId = plan.PlanId,
                TransactionId = transactionId,
                Files = plan.Files.Select(c => new JournalFile(c.FileName, c.Before?.Sha512, c.Before?.Size ?? 0,
                    c.File.Hashes["sha512"], c.File.Size)).ToList()
            };
            foreach (var file in journal.Files.Where(f => f.BeforeHash != null))
                await CopyCheckedAsync(ModPath(plan.GameDir, file.FileName), SafePath(Path.Combine(backup, file.FileName)),
                    file.BeforeHash!, file.BeforeSize, cancellationToken);
            await WriteJournalAsync(plan.GameDir, journal, cancellationToken);
            bool committed = false;
            try
            {
                foreach (var file in journal.Files)
                {
                    if (isRunning()) throw Error("busy", "Сначала закройте игру.");
                    cancellationToken.ThrowIfCancellationRequested();
                    string destination = ModPath(plan.GameDir, file.FileName);
                    if (file.BeforeHash == null ? File.Exists(destination) || Directory.Exists(destination) :
                        !await MatchesAsync(destination, file.BeforeHash, file.BeforeSize, cancellationToken))
                        throw Error("conflict", $"Файл изменился после проверки: {file.FileName}");
                    string prepared = SafePath(Path.Combine(preparedMods, file.FileName));
                    if (!await MatchesAsync(prepared, file.AfterHash, file.AfterSize, cancellationToken))
                        throw Error("hash_mismatch", $"Повреждена загрузка: {file.FileName}");
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Move(prepared, destination, overwrite: file.BeforeHash != null);
                }
                cancellationToken.ThrowIfCancellationRequested();
                journal.Committed = true;
                await WriteJournalAsync(plan.GameDir, journal, CancellationToken.None);
                committed = true;
                File.Delete(JournalPath(plan.GameDir));
                return new(plan.Files.Count, backup);
            }
            catch (Exception original)
            {
                if (committed) throw;
                try { await RollbackAsync(plan.GameDir, journal); }
                catch (Exception rollback)
                {
                    throw Error("rollback_failed", $"Не удалось завершить откат. Копии файлов: {backup}", new AggregateException(original, rollback));
                }
                throw;
            }
        }
        finally { TryDeletePrepared(game); }
    }

    public async Task RecoverAsync(string gameDir, Func<bool> isRunning, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(isRunning);
        string directory = SafePath(gameDir);
        if (!File.Exists(JournalPath(directory))) return;
        if (isRunning()) throw Error("busy", "Сначала закройте игру.");
        using var instanceLock = ServerModSync.AcquireInstanceLock(directory);
        if (File.Exists(SafePath(Path.Combine(directory, ".mechanica", "server-sync-journal.json"))))
            throw Error("busy", "Сначала нужно восстановить серверную сборку.");
        cancellationToken.ThrowIfCancellationRequested();
        var info = new FileInfo(JournalPath(directory));
        if (!info.Exists) return;
        if (info.Length > 8 * 1024 * 1024) throw Error("conflict", "Повреждён журнал обновления модов.");
        UpdateJournal? journal;
        try { journal = JsonSerializer.Deserialize<UpdateJournal>(await File.ReadAllTextAsync(info.FullName, cancellationToken), JournalOptions); }
        catch (JsonException ex) { throw Error("conflict", "Повреждён журнал обновления модов.", ex); }
        if (journal == null || journal.Version != 1 || journal.PlanId == Guid.Empty || journal.TransactionId == Guid.Empty || journal.Files == null ||
            journal.Files.Count is 0 or > 2048 || journal.Files.Any(f => f == null || FileName(f.FileName) != f.FileName ||
                !ValidHash(f.AfterHash, 128) || f.AfterSize <= 0 || f.BeforeHash != null && (!ValidHash(f.BeforeHash, 128) || f.BeforeSize <= 0)) ||
            journal.Files.Select(f => f.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != journal.Files.Count)
            throw Error("conflict", "Повреждён журнал обновления модов.");
        if (isRunning()) throw Error("busy", "Сначала закройте игру.");
        if (journal.Committed) File.Delete(JournalPath(directory));
        else await RollbackAsync(directory, journal);
    }

    private static async Task RollbackAsync(string gameDir, UpdateJournal journal)
    {
        var errors = new List<Exception>();
        foreach (var file in journal.Files.AsEnumerable().Reverse())
        {
            try
            {
                string destination = ModPath(gameDir, file.FileName);
                if (file.BeforeHash != null && await MatchesAsync(destination, file.BeforeHash, file.BeforeSize, CancellationToken.None)) continue;
                if (File.Exists(destination) && !await MatchesAsync(destination, file.AfterHash, file.AfterSize, CancellationToken.None))
                    throw Error("conflict", $"Файл изменён после установки: {file.FileName}");
                if (file.BeforeHash == null)
                {
                    if (File.Exists(destination)) File.Delete(destination);
                    continue;
                }
                string backup = SafePath(Path.Combine(TransactionPath(gameDir, journal.TransactionId), "backup", file.FileName));
                if (!await MatchesAsync(backup, file.BeforeHash, file.BeforeSize, CancellationToken.None))
                    throw Error("conflict", $"Резервная копия повреждена: {file.FileName}");
                string temporary = SafePath(destination + "." + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    await CopyCheckedAsync(backup, temporary, file.BeforeHash, file.BeforeSize, CancellationToken.None);
                    File.Move(temporary, destination, overwrite: true);
                }
                finally { AtomicFile.TryDelete(temporary); }
            }
            catch (Exception ex) { errors.Add(ex); }
        }
        if (errors.Count > 0) throw new AggregateException("Не удалось восстановить все моды.", errors);
        File.Delete(JournalPath(gameDir));
    }

    private static async Task ValidateManagedAsync(ModUpdatePlan plan, CancellationToken ct)
    {
        var managed = await ServerModSync.GetManagedFilesAsync(plan.GameDir, ct);
        foreach (var file in plan.Files)
            if (managed.Any(m => m.FileName.Equals(BaseFileName(file.FileName), StringComparison.OrdinalIgnoreCase)) ||
                file.Before != null && IsManaged(file.Before, managed))
                throw Error("conflict", $"Файл принадлежит серверной сборке: {file.FileName}");
    }

    private static async Task<List<ModUpdateLocal>> ReadLocalAsync(string gameDir, CancellationToken ct)
    {
        var files = new List<ModUpdateLocal>();
        string mods = SafePath(Path.Combine(gameDir, "mods"));
        foreach (var file in ModInstaller.GetInstalledMods(mods))
        {
            ct.ThrowIfCancellationRequested();
            string path = SafePath(file.FilePath);
            string name = FileName(Path.GetFileName(path));
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                string sha1 = Convert.ToHexString(await SHA1.HashDataAsync(stream, ct)).ToLowerInvariant();
                stream.Position = 0;
                string sha512 = Convert.ToHexString(await SHA512.HashDataAsync(stream, ct)).ToLowerInvariant();
                files.Add(new(path, name, file.Enabled, stream.Length, sha1, sha512));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { files.Add(new(path, name, file.Enabled, 0, "", "", State: ModUpdateState.Unavailable, Reason: "Не удалось прочитать файл.")); }
        }
        return files;
    }

    private static async Task ValidateSnapshotAsync(ModUpdateScan scan, CancellationToken ct)
    {
        var actual = await ReadLocalAsync(scan.GameDir, ct);
        if (actual.Count != scan.Files.Count || scan.Files.Any(expected => !actual.Any(f =>
            f.FilePath.Equals(expected.FilePath, StringComparison.OrdinalIgnoreCase) && f.Size == expected.Size &&
            Same(f.Sha512, expected.Sha512))))
            throw Error("conflict", "Состав модов изменился после проверки. Проверьте обновления ещё раз.");
    }

    private static void ValidateInstance(ModUpdateScan scan)
    {
        var instance = scan.Instance;
        if (instance.Id != scan.InstanceId || instance.McVersion != scan.Minecraft ||
            instance.Loader.ToString().ToLowerInvariant() != scan.Loader || instance.LoaderVersion != scan.LoaderVersion)
            throw Error("conflict", "Настройки сборки изменились после проверки.");
    }

    private static void EnsureNoJournal(string gameDir)
    {
        if (File.Exists(JournalPath(gameDir))) throw Error("busy", "Сначала нужно восстановить предыдущее обновление модов.");
    }
    private static string JournalPath(string gameDir) => SafePath(Path.Combine(gameDir, ".mechanica", "mod-update-journal.json"));
    private static string TransactionPath(string gameDir, Guid id) => SafePath(Path.Combine(gameDir, ".mechanica", "mod-updates", id.ToString("N")));
    private static string ModPath(string gameDir, string name) => SafePath(Path.Combine(gameDir, "mods", FileName(name)));

    private static async Task WriteJournalAsync(string gameDir, UpdateJournal journal, CancellationToken ct)
    {
        string path = JournalPath(gameDir);
        string temporary = SafePath(path + "." + Guid.NewGuid().ToString("N") + ".tmp");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(journal, JournalOptions);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                await stream.WriteAsync(bytes, ct);
                stream.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally { AtomicFile.TryDelete(temporary); }
    }

    private static async Task CopyCheckedAsync(string source, string destination, string sha512, long size, CancellationToken ct)
    {
        SafePath(source);
        SafePath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
        await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
        {
            if (input.Length != size) throw Error("conflict", "Файл изменился после проверки.");
            await input.CopyToAsync(output, ct);
        }
        if (!await MatchesAsync(destination, sha512, size, ct)) throw Error("conflict", "Файл изменился после проверки.");
    }

    private static void TryDeletePrepared(string path)
    {
        try { DeletePrepared(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ModUpdateException) { }

        static void DeletePrepared(string directory)
        {
            SafePath(directory);
            if (!Directory.Exists(directory)) return;
            foreach (string child in Directory.EnumerateDirectories(directory)) DeletePrepared(child);
            foreach (string file in Directory.EnumerateFiles(directory)) File.Delete(SafePath(file));
            Directory.Delete(directory);
        }
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var input = new FileStream(SafePath(path), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        return Convert.ToHexString(await SHA512.HashDataAsync(input, ct)).ToLowerInvariant();
    }
    private static async Task<bool> MatchesAsync(string path, string sha512, long size, CancellationToken ct) =>
        File.Exists(SafePath(path)) && new FileInfo(path).Length == size && Same(await HashAsync(path, ct), sha512);

    private static string SafePath(string path)
    {
        string full = Path.GetFullPath(path);
        for (string? current = full; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw Error("conflict", "Ссылки в путях сборки не поддерживаются.");
        return full;
    }
    private static string FileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw Error("incompatible", "Недопустимое имя файла мода.");
        string stem = name.Split('.')[0].ToUpperInvariant();
        if (name.Length > 240 || Path.GetFileName(name) != name || name.TrimEnd(' ', '.') != name ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains(':') ||
            !BaseFileName(name).EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
            stem is "CON" or "PRN" or "AUX" or "NUL" || stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '1' and <= '9')
            throw Error("incompatible", "Недопустимое имя файла мода.");
        return name;
    }
    private sealed record JournalFile(string FileName, string? BeforeHash, long BeforeSize, string AfterHash, long AfterSize);
    private sealed class UpdateJournal
    {
        public int Version { get; init; } = 1;
        public Guid PlanId { get; init; }
        public Guid TransactionId { get; init; }
        public bool Committed { get; set; }
        public List<JournalFile> Files { get; init; } = [];
    }
}
