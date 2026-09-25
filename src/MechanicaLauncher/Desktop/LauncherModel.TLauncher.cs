using MechanicaLauncher.Core.Security;

namespace MechanicaLauncher.Desktop;

public sealed partial class LauncherModel
{
    private readonly CancellationTokenSource tlauncherLifetime = new();
    private TLauncherScanResult? tlauncherScan;
    private TLauncherCleanupResult? tlauncherCleanup;
    private string? tlauncherBackup;
    private bool tlauncherCheckRequired;
    private string tlauncherCleanupStatus = "", tlauncherScanError = "";
    private string[] tlauncherUnverified = [];
    internal Func<IReadOnlyCollection<string>, CancellationToken, TLauncherScanResult>? TLauncherScanner { get; set; }
    internal Func<TLauncherScanResult, string, IProgress<string>, CancellationToken, Task<TLauncherCleanupResult>>? TLauncherRemover { get; set; }
    public bool ScanningTLauncher { get; private set; }
    public bool TLauncherDetected => tlauncherScan?.IsDetected == true;
    public bool TLauncherBlocked => tlauncherCheckRequired || ScanningTLauncher || tlauncherScan?.HasCleanupItems == true || tlauncherUnverified.Length > 0;
    public bool HasTLauncherFiles => tlauncherScan?.HasCleanupItems == true;
    public string TLauncherGateTitle => ScanningTLauncher || tlauncherCheckRequired && tlauncherScanError.Length == 0
        ? T("Проверка TLauncher", "Checking for TLauncher") : tlauncherScanError.Length > 0
        ? T("Проверка не завершена", "Check incomplete") : tlauncherUnverified.Length > 0 && !HasTLauncherFiles
        ? T("Нужна повторная проверка", "Another check is needed") : T("Обнаружен TLauncher", "TLauncher detected");
    public string TLauncherGateDescription => ScanningTLauncher || tlauncherCheckRequired && tlauncherScanError.Length == 0
        ? ""
        : tlauncherScanError.Length > 0 || tlauncherUnverified.Length > 0 && !HasTLauncherFiles
        ? ""
        : T("Удали TLauncher, чтобы продолжить.", "Remove TLauncher to continue.");
    public bool HasTLauncherReport => tlauncherScan != null;
    public bool HasTLauncherBackup => tlauncherBackup != null;
    public bool HasTLauncherRegistration => tlauncherScan?.Installations.Count > 0;
    public string TLauncherStatus => ScanningTLauncher ? T("Проверяю файлы…", "Checking files…") : tlauncherScanError.Length > 0 ? tlauncherScanError :
        tlauncherCleanupStatus.Length > 0 ? tlauncherCleanupStatus :
        tlauncherUnverified.Length > 0 ? T("Часть файлов недоступна.", "Some files are unavailable.") :
        tlauncherScan == null ? "" : TLauncherDetected ? T("TLauncher найден", "TLauncher found") :
        tlauncherScan.Warnings.Count > 0 ? T("Есть пропуски", "Some items were skipped") : T("TLauncher не найден", "TLauncher not found");

    public void CheckTLauncher() => Run(() => ScanTLauncherAsync());
    internal void BeginTLauncherCheck() { tlauncherCheckRequired = true; Changed(); }
    public void SelectTLauncherFolder()
    {
        if (ScanningTLauncher || DialogBusy) return;
        string? directory = Platform.PickFolder(T("Папка TLauncher", "TLauncher folder"));
        if (directory != null) Run(() => ScanTLauncherAsync(directory));
    }
    internal async Task ScanTLauncherAsync(string? directory = null)
    {
        if (ScanningTLauncher || DialogBusy) return;
        ScanningTLauncher = true; tlauncherCheckRequired = true; tlauncherCleanupStatus = ""; tlauncherScanError = ""; Changed();
        try
        {
            if (directory != null)
            {
                if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException(T("Выбери локальную папку лаунчера.", "Choose a local launcher folder."));
                Settings.TLauncherScanDirectories = (Settings.TLauncherScanDirectories ?? []).Append(Path.GetFullPath(directory)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                Settings.Save();
            }
            var scan = await ReadTLauncherScan();
            if (disposed) return;
            ApplyTLauncherScan(scan);
        }
        catch (OperationCanceledException) when (tlauncherLifetime.IsCancellationRequested) { }
        catch (Exception ex) { tlauncherScanError = T("Не удалось проверить файлы: ", "Could not check files: ") + ex.Message; }
        finally { ScanningTLauncher = false; if (!disposed) Changed(); }
    }
    private Task<TLauncherScanResult> ReadTLauncherScan()
    {
        string[] folders = (Settings.TLauncherScanDirectories ?? []).Concat((Settings.KnownTLauncherFiles ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path)).Select(Path.GetDirectoryName).OfType<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        string[] knownDirectories = (Settings.KnownTLauncherDirectories ?? []).ToArray();
        return Task.Run(() => TLauncherScanner?.Invoke(folders, tlauncherLifetime.Token) ?? TLauncherDetector.Scan(folders, tlauncherLifetime.Token, knownDirectories));
    }
    private void ApplyTLauncherScan(TLauncherScanResult scan)
    {
        tlauncherUnverified = tlauncherUnverified.Concat(Settings.KnownTLauncherFiles ?? []).Concat(tlauncherScan?.Files.Select(f => f.Path) ?? []).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => File.Exists(path) && !scan.Files.Any(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) &&
                !scan.ProtectedPaths.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase) || path.StartsWith(Path.TrimEndingDirectorySeparator(p) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))).ToArray();
        string[] unavailableDirectories = (Settings.KnownTLauncherDirectories ?? []).Where(root => Directory.Exists(root) &&
            !scan.OwnedDirectories.Contains(root, StringComparer.OrdinalIgnoreCase) && scan.Warnings.Any(w => w.Contains(root, StringComparison.OrdinalIgnoreCase)) &&
            !scan.ProtectedPaths.Any(p => p.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                (p.Contains("legacy", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)))).ToArray();
        tlauncherUnverified = tlauncherUnverified.Concat(unavailableDirectories).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        string[] owned = scan.OwnedDirectories.Concat(unavailableDirectories).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        string[] known = scan.Files.Where(f => !scan.OwnedDirectories.Any(root => f.Path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            .Select(f => f.Path).Concat(tlauncherUnverified).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (!(Settings.KnownTLauncherFiles ?? []).SequenceEqual(known, StringComparer.OrdinalIgnoreCase) ||
            !(Settings.KnownTLauncherDirectories ?? []).SequenceEqual(owned, StringComparer.OrdinalIgnoreCase))
        {
            Settings.KnownTLauncherFiles = known; Settings.KnownTLauncherDirectories = owned; Settings.Save();
        }
        tlauncherScan = scan; tlauncherCheckRequired = false; tlauncherScanError = "";
    }
    public void ReviewTLauncher()
    {
        if (tlauncherScan is not { } scan || DialogBusy || ScanningTLauncher) return;
        var items = scan.OwnedDirectories.Select(directory => new ItemModel
        {
            Id = directory, Title = Path.GetFileName(directory), Meta = directory,
            Action = () => Platform.OpenPath(directory)
        }).Concat(scan.Files.Where(file => !scan.OwnedDirectories.Any(directory => file.Path.StartsWith(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))).Select(file => new ItemModel
        {
            Id = file.Path, Title = Path.GetFileName(file.Path), Meta = file.Path,
            Action = () => Platform.OpenPath(Path.GetDirectoryName(file.Path)!)
        })).Concat(scan.RegistryEntries.Select(entry => new ItemModel { Id = entry.Id, Title = entry.Name, Meta = entry.Path }))
            .Concat(scan.Processes.Select(process => new ItemModel { Id = "process-" + process.Id, Title = T("Закрыть TLauncher", "Close TLauncher"), Meta = Path.GetFileName(process.Executable) }));
        ShowChoices(T("Удалить TLauncher?", "Remove TLauncher?"), items);
        DialogBody = ""; DialogSearch = false;
        DialogPrimary = scan.HasCleanupItems ? T("Удалить", "Remove") : "";
        DialogDanger = scan.HasCleanupItems;
        dialogAction = async () =>
        {
            if (Sessions.Preparing || Sessions.RunningCount > 0 || Sessions.Downloads.HasPending)
                throw new InvalidOperationException(T("Заверши игру и дождись загрузок перед очисткой.", "Finish the game and pending downloads before cleaning."));
            bool cleaning = true;
            var progress = new Progress<string>(message => { if (!disposed && cleaning) { DialogBody = message; Changed(); } });
            TLauncherCleanupResult result;
            try
            {
                result = await Task.Run(() => (TLauncherRemover ?? TLauncherCleanupProcess.CleanAsync)(scan,
                    Path.Combine(LauncherPaths.DataDirectory, "backups", "tlauncher"), progress, tlauncherLifetime.Token));
            }
            finally { cleaning = false; DialogBody = ""; Changed(); }
            tlauncherCleanup = result;
            tlauncherBackup = result.BackupDirectory ?? tlauncherBackup;
            tlauncherCheckRequired = true;
            try { ApplyTLauncherScan(await ReadTLauncherScan()); }
            catch (Exception ex)
            {
                tlauncherScanError = T("Не удалось повторно проверить файлы: ", "Could not recheck files: ") + ex.Message;
                throw;
            }
            tlauncherCleanupStatus = T($"Удалено: {result.Removed.Count}. Пропущено: {result.Skipped.Count}.", $"Removed: {result.Removed.Count}. Skipped: {result.Skipped.Count}.");
            Notice(tlauncherCleanupStatus, result.Skipped.Count > 0); Changed();
        };
        Changed();
    }
    public void ShowTLauncherReport()
    {
        if (tlauncherScan is not { } scan) return;
        ShowText(T("Проверка TLauncher", "TLauncher scan"), string.Join("\n\n", new[]
        {
            TLauncherStatus,
            T("Последняя очистка — удалено:\n", "Last cleanup — removed:\n") + string.Join('\n', tlauncherCleanup?.Removed ?? []),
            T("Последняя очистка — пропущено:\n", "Last cleanup — skipped:\n") + string.Join('\n', tlauncherCleanup?.Skipped ?? []),
            T("Ранее найденные файлы, требующие повторной проверки:\n", "Previously detected files requiring another check:\n") + string.Join('\n', tlauncherUnverified),
            T("Файлы для очистки:\n", "Files to clean:\n") + string.Join('\n', scan.Files.Select(f => f.Path + " · " + f.Reason)),
            T("Защищённые папки и файлы:\n", "Protected folders and files:\n") + string.Join('\n', scan.ProtectedPaths),
            T("Пропущено при проверке:\n", "Skipped during scan:\n") + string.Join('\n', scan.Warnings),
            T("Папки:\n", "Folders:\n") + string.Join('\n', scan.OwnedDirectories),
            T("Записи Windows:\n", "Windows entries:\n") + string.Join('\n', scan.RegistryEntries.Select(i => i.Path))
        }));
    }
    public void OpenTLauncherBackup() { if (tlauncherBackup != null) Platform.OpenPath(tlauncherBackup); }
    public void OpenInstalledApps() => Process.Start(new ProcessStartInfo("ms-settings:appsfeatures") { UseShellExecute = true });
}
