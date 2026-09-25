using System.Security.Cryptography;
using System.Text.Json;
using MechanicaLauncher.Core.IO;

namespace MechanicaLauncher.Core.Security;

public sealed record TLauncherCleanupResult(string? BackupDirectory, IReadOnlyList<string> Removed, IReadOnlyList<string> Skipped);

public static class TLauncherCleaner
{
    public static bool RequiresElevation(TLauncherScanResult scan) => OperatingSystem.IsWindows() && TLauncherWindows.RequiresElevation(scan);

    public static async Task<TLauncherCleanupResult> CleanAsync(TLauncherScanResult approved, string backupRoot,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approved);
        if (!Path.IsPathFullyQualified(backupRoot)) throw new ArgumentException("Папка резервной копии должна иметь абсолютный путь.", nameof(backupRoot));
        backupRoot = Path.GetFullPath(backupRoot);
        if (approved.Roots.Any(root => TLauncherDetector.IsWithin(backupRoot, root) || backupRoot.Equals(root, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Резервная копия должна находиться вне очищаемых папок.");
        var skipped = new List<string>();
        if (OperatingSystem.IsWindows())
            foreach (var process in approved.Processes)
            {
                var proof = approved.Files.FirstOrDefault(f => f.Path.Equals(process.LauncherFile, StringComparison.OrdinalIgnoreCase));
                if (proof == null || !TLauncherDetector.IsRegularPath(proof.Path) || !await Matches(proof.Path, proof, cancellationToken).ConfigureAwait(false)) continue;
                try { await TLauncherWindows.StopProcess(process, cancellationToken).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
                { skipped.Add("Процесс TLauncher: " + ex.Message); }
            }
        var rescanned = TLauncherDetector.Rescan(approved, cancellationToken);
        var current = rescanned.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var candidates = new Dictionary<string, TLauncherFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in approved.Files)
        {
            if (file.Shortcut)
            {
                if (TLauncherDetector.IsRegularPath(file.Path) && await Matches(file.Path, file, cancellationToken).ConfigureAwait(false)) candidates[file.Path] = file;
                else skipped.Add(file.Path + ": ярлык изменился");
            }
            else if (current.TryGetValue(file.Path, out var now) && now.Sha256 == file.Sha256 && now.Length == file.Length) candidates[file.Path] = now;
            else if (!approved.OwnedDirectories.Any(root => TLauncherDetector.IsWithin(file.Path, root))) skipped.Add(file.Path + ": файл изменился или больше не подтверждён");
        }
        foreach (var file in rescanned.Files.Where(f => rescanned.OwnedDirectories.Any(root => approved.OwnedDirectories.Contains(root, StringComparer.OrdinalIgnoreCase) && TLauncherDetector.IsWithin(f.Path, root))))
            candidates[file.Path] = file;
        var directories = rescanned.Directories.Where(d => approved.OwnedDirectories.Any(root => d.Equals(root, StringComparison.OrdinalIgnoreCase) || TLauncherDetector.IsWithin(d, root)))
            .Concat(approved.Directories.Where(d => approved.Files.Any(f => f.Shortcut && TLauncherDetector.IsWithin(f.Path, d))))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(d => d.Length).ToArray();
        var registry = new List<TLauncherRegistryEntry>();
        if (OperatingSystem.IsWindows())
            foreach (var entry in approved.RegistryEntries)
            {
                try
                {
                    string? snapshot = TLauncherWindows.CurrentSnapshot(entry);
                    if (snapshot == entry.Snapshot) registry.Add(entry);
                    else if (snapshot != null) skipped.Add(entry.Path + ": запись изменилась");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { skipped.Add(entry.Path + ": " + ex.Message); }
            }
        if (candidates.Count == 0 && registry.Count == 0 && directories.Length == 0) return new(null, [], skipped);
        string? existingParent = backupRoot;
        while (existingParent != null && !Directory.Exists(existingParent)) existingParent = Path.GetDirectoryName(existingParent);
        if (existingParent == null || !TLauncherDetector.IsRegularPath(existingParent)) throw new IOException("Небезопасный путь резервной копии.");
        Directory.CreateDirectory(backupRoot);
        if (!TLauncherDetector.IsRegularPath(backupRoot)) throw new IOException("Резервная копия не может находиться в папке-ссылке.");
        string backup = Path.Combine(backupRoot, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backup);
        var manifest = new List<object>();
        var plannedFiles = candidates.Values.ToArray();
        for (int i = 0; i < plannedFiles.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = plannedFiles[i];
            progress?.Report($"Копирование {i + 1}/{plannedFiles.Length}");
            if (!TLauncherDetector.IsRegularPath(file.Path)) throw new IOException("Путь изменился. Очистка не выполнена: " + file.Path);
            string saved = $"{i + 1:D4}.bin";
            await using (var input = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
            await using (var output = new FileStream(Path.Combine(backup, saved), FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            if (!await Matches(Path.Combine(backup, saved), file, cancellationToken).ConfigureAwait(false))
                throw new IOException("Файл изменился при копировании. Оригиналы сохранены: " + file.Path);
            manifest.Add(new { originalPath = file.Path, backupFile = saved, file.Length, file.Sha256, attributes = File.GetAttributes(file.Path) });
        }
        if (OperatingSystem.IsWindows())
            AtomicFile.WriteText(Path.Combine(backup, "registry.json"), JsonSerializer.Serialize(registry.Select(TLauncherWindows.RegistryBackup), new JsonSerializerOptions { WriteIndented = true }));
        AtomicFile.WriteText(Path.Combine(backup, "manifest.json"), JsonSerializer.Serialize(new { createdUtc = DateTime.UtcNow, state = "backup-complete", files = manifest, directories }, new JsonSerializerOptions { WriteIndented = true }));
        var removed = new List<string>();
        foreach (var file in plannedFiles)
        {
            if (cancellationToken.IsCancellationRequested) { skipped.Add(file.Path + ": отменено"); continue; }
            try
            {
                progress?.Report("Удаление: " + Path.GetFileName(file.Path));
                if (!file.Shortcut && !approved.Roots.Any(root => TLauncherDetector.IsWithin(file.Path, root)) || IsProtected(file.Path) ||
                    !TLauncherDetector.IsRegularPath(file.Path) || !await Matches(file.Path, file, cancellationToken).ConfigureAwait(false))
                { skipped.Add(file.Path + ": файл или путь изменился"); continue; }
                var attributes = File.GetAttributes(file.Path);
                if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(file.Path, attributes & ~FileAttributes.ReadOnly);
                File.Delete(file.Path);
                removed.Add(file.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
            { skipped.Add(file.Path + ": " + ex.Message); }
        }
        foreach (string directory in directories)
        {
            if (cancellationToken.IsCancellationRequested) { skipped.Add(directory + ": отменено"); continue; }
            if (!Directory.Exists(directory)) continue;
            if (!TLauncherDetector.IsRegularPath(directory) || IsProtected(directory)) { skipped.Add(directory + ": путь изменился"); continue; }
            try
            {
                var remaining = Directory.EnumerateFileSystemEntries(directory).ToArray();
                if (remaining.Length > 0)
                {
                    if (remaining.Any(p => !TLauncherDetector.IsGamePath(p) && !TLauncherDetector.IsLegacyPath(p))) skipped.Add(directory + ": остались файлы");
                    continue;
                }
                Directory.Delete(directory, recursive: false);
                removed.Add(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped.Add(directory + ": " + ex.Message); }
        }
        if (OperatingSystem.IsWindows())
            foreach (var entry in registry)
            {
                if (cancellationToken.IsCancellationRequested) { skipped.Add(entry.Path + ": отменено"); continue; }
                try
                {
                    progress?.Report("Удаление: " + entry.Name);
                    if (entry.Value == null && plannedFiles.Any(f => !f.Shortcut && File.Exists(f.Path)))
                    { skipped.Add(entry.Path + ": остались файлы приложения"); continue; }
                    if (TLauncherWindows.RemoveRegistryEntry(entry)) removed.Add(entry.Path);
                    else skipped.Add(entry.Path + ": запись изменилась");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { skipped.Add(entry.Path + ": " + ex.Message); }
            }
        AtomicFile.WriteText(Path.Combine(backup, "result.json"), JsonSerializer.Serialize(new { removed, skipped }, new JsonSerializerOptions { WriteIndented = true }));
        return new(backup, removed, skipped);

        bool IsProtected(string path) => TLauncherDetector.IsLegacyPath(path) || TLauncherDetector.IsGamePath(path) ||
            rescanned.ProtectedPaths.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase) || TLauncherDetector.IsWithin(path, p)) ||
            !rescanned.OwnedDirectories.Any(root => path.Equals(root, StringComparison.OrdinalIgnoreCase) || TLauncherDetector.IsWithin(path, root)) && TLauncherDetector.IsProtectedPath(path);
    }

    private static async Task<bool> Matches(string path, TLauncherFile expected, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        return stream.Length == expected.Length && Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)) == expected.Sha256;
    }
}
