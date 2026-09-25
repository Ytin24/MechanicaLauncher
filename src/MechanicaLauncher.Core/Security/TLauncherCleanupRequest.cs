using System.Text.Json;
using MechanicaLauncher.Core.IO;

namespace MechanicaLauncher.Core.Security;

public sealed record TLauncherCleanupRequest(string[] Roots, TLauncherFile[] Files, string[] OwnedDirectories,
    Dictionary<string, string> Registry, TLauncherProcess[] Processes, string BackupRoot)
{
    public static TLauncherCleanupRequest Create(TLauncherScanResult scan, string backupRoot) => new(
        scan.Roots.ToArray(), scan.Files.ToArray(), scan.OwnedDirectories.ToArray(),
        scan.RegistryEntries.ToDictionary(entry => entry.Id, entry => entry.Snapshot, StringComparer.OrdinalIgnoreCase), scan.Processes.ToArray(), backupRoot);

    public async Task<TLauncherCleanupResult> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var current = TLauncherDetector.Scan(Roots, cancellationToken);
        var approved = Resolve(current, out var skipped);
        var result = await TLauncherCleaner.CleanAsync(approved, BackupRoot, cancellationToken: cancellationToken).ConfigureAwait(false);
        result = result with { Skipped = skipped.Concat(result.Skipped).ToArray() };
        if (result.BackupDirectory != null)
            AtomicFile.WriteText(Path.Combine(result.BackupDirectory, "result.json"), JsonSerializer.Serialize(new { removed = result.Removed, skipped = result.Skipped }));
        return result;
    }

    internal TLauncherScanResult Resolve(TLauncherScanResult current, out List<string> skipped)
    {
        var owned = current.OwnedDirectories.Where(root => OwnedDirectories.Contains(root, StringComparer.OrdinalIgnoreCase)).ToArray();
        var expectedFiles = Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        var files = current.Files.Where(file => expectedFiles.TryGetValue(file.Path, out var expected) &&
            file.Sha256 == expected.Sha256 && file.Length == expected.Length ||
            owned.Any(root => TLauncherDetector.IsWithin(file.Path, root))).ToArray();
        var selectedPaths = files.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool unapprovedFiles = current.Files.Any(file => !selectedPaths.Contains(file.Path));
        var registry = current.RegistryEntries.Where(entry => Registry.TryGetValue(entry.Id, out var snapshot) && entry.Snapshot == snapshot &&
            (!unapprovedFiles || entry.Value != null)).ToArray();
        var directories = current.Directories.Where(directory => owned.Any(root => directory.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            TLauncherDetector.IsWithin(directory, root)) || files.Any(file => file.Shortcut && TLauncherDetector.IsWithin(file.Path, directory))).ToArray();
        var processes = current.Processes.Where(process => Processes.Contains(process)).ToArray();
        skipped = Files.Where(file => File.Exists(file.Path) && !selectedPaths.Contains(file.Path))
            .Select(file => file.Path + ": файл изменился или больше не подтверждён").ToList();
        skipped.AddRange(OwnedDirectories.Where(root => Directory.Exists(root) && !owned.Contains(root, StringComparer.OrdinalIgnoreCase))
            .Select(root => root + ": принадлежность папки изменилась"));
        skipped.AddRange(Registry.Keys.Where(id => !registry.Any(entry => entry.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            .Select(id => id + ": запись изменилась или уже удалена"));
        return new(files, current.Installations, current.ProtectedPaths, current.Warnings, Roots, owned, directories, registry, processes);
    }
}
