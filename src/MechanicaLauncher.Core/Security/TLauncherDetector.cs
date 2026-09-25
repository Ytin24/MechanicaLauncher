using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace MechanicaLauncher.Core.Security;

public sealed record TLauncherFile(string Path, string Reason, long Length, string Sha256, bool Shortcut = false);
public sealed record TLauncherInstallation(string Name, string RegistryPath, string? Directory, RegistryHive Hive = RegistryHive.CurrentUser, RegistryView View = RegistryView.Default, string? SubKey = null);

public sealed class TLauncherScanResult
{
    public IReadOnlyList<TLauncherFile> Files { get; }
    public IReadOnlyList<TLauncherInstallation> Installations { get; }
    public IReadOnlyList<string> ProtectedPaths { get; }
    public IReadOnlyList<string> Warnings { get; }
    public IReadOnlyList<string> OwnedDirectories { get; }
    public IReadOnlyList<TLauncherRegistryEntry> RegistryEntries { get; }
    public IReadOnlyList<TLauncherProcess> Processes { get; }
    public bool HasCleanupItems => Files.Count > 0 || Directories.Count > 0 || RegistryEntries.Count > 0;
    public bool IsDetected => HasCleanupItems || Installations.Count > 0;
    internal IReadOnlyList<string> Roots { get; }
    internal IReadOnlyList<string> Directories { get; }

    internal TLauncherScanResult(IEnumerable<TLauncherFile> files, IEnumerable<TLauncherInstallation> installations,
        IEnumerable<string> protectedPaths, IEnumerable<string> warnings, IEnumerable<string> roots,
        IEnumerable<string>? ownedDirectories = null, IEnumerable<string>? directories = null,
        IEnumerable<TLauncherRegistryEntry>? registryEntries = null, IEnumerable<TLauncherProcess>? processes = null)
    {
        Files = Array.AsReadOnly(files.ToArray()); Installations = Array.AsReadOnly(installations.ToArray());
        ProtectedPaths = Array.AsReadOnly(protectedPaths.ToArray()); Warnings = Array.AsReadOnly(warnings.ToArray());
        Roots = Array.AsReadOnly(roots.ToArray());
        OwnedDirectories = Array.AsReadOnly((ownedDirectories ?? []).ToArray());
        Directories = Array.AsReadOnly((directories ?? []).ToArray());
        RegistryEntries = Array.AsReadOnly((registryEntries ?? []).ToArray());
        Processes = Array.AsReadOnly((processes ?? []).ToArray());
    }
}

public static class TLauncherDetector
{
    private static readonly HashSet<string> SharedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "saves", "mods", "modpacks", "versions", "assets", "libraries", "runtime", "jre", "jre_default", "mojang_jre",
        "resourcepacks", "shaderpacks", "screenshots", "config", "logs", "crash-reports", "webcache", "cache"
    };
    private static readonly HashSet<string> GameDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "saves", "mods", "modpacks", "versions", "assets", "resourcepacks", "shaderpacks", "screenshots", "config"
    };

    public static TLauncherScanResult Scan(CancellationToken cancellationToken = default)
        => Scan([], cancellationToken);

    public static TLauncherScanResult Scan(IEnumerable<string> additionalDirectories, CancellationToken cancellationToken = default, IEnumerable<string>? knownDirectories = null)
    {
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var warnings = new List<string>();
        var installations = OperatingSystem.IsWindows() ? ReadInstallations(warnings) : [];
        var roots = new List<string> { Path.Combine(roaming, ".tlauncher"), Path.Combine(roaming, ".minecraft") };
        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
                     Environment.SpecialFolder.LocalApplicationData })
        {
            string parent = Environment.GetFolderPath(folder);
            if (parent.Length > 0) roots.Add(Path.Combine(parent, "TLauncher"));
        }
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "TLauncher"));
        roots.AddRange(installations.Select(i => i.Directory).OfType<string>().Where(IsLocalRoot));
        roots.AddRange(additionalDirectories.Where(IsLocalRoot));
        roots.AddRange((knownDirectories ?? []).Where(IsLocalRoot));
        string settings = Path.Combine(roaming, ".tlauncher", "tlauncher-2.0.properties");
        try
        {
            if (IsRegularPath(settings) && new FileInfo(settings).Length <= 1024 * 1024 && !IsLegacyIdentity(File.ReadAllText(settings)))
                foreach (string line in File.ReadLines(settings))
                {
                    var match = Regex.Match(line, @"^\s*minecraft\.gamedir\s*[=:]\s*(.+)$");
                    if (!match.Success) continue;
                    string path = Regex.Replace(match.Groups[1].Value.Trim(), @"\\(u[\da-fA-F]{4}|.)", m =>
                        m.Groups[1].Value is var value && value.StartsWith('u') ? ((char)Convert.ToInt32(value[1..], 16)).ToString() : m.Groups[1].Value);
                    if (IsLocalRoot(path)) roots.Add(path);
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { warnings.Add("Не удалось прочитать настройки TLauncher."); }
        var scan = ScanRoots(roots, installations, warnings, cancellationToken, knownDirectories);
        return OperatingSystem.IsWindows() ? TLauncherWindows.AddArtifacts(scan, cancellationToken: cancellationToken) : scan;
    }

    public static TLauncherScanResult ScanFolder(string directory, CancellationToken cancellationToken = default)
    {
        if (!IsLocalRoot(directory)) throw new ArgumentException("Выбери локальную папку лаунчера.", nameof(directory));
        return ScanRoots([directory], [], [], cancellationToken);
    }

    internal static TLauncherScanResult Rescan(TLauncherScanResult scan, CancellationToken cancellationToken)
        => ScanRoots(scan.Roots, scan.Installations, [], cancellationToken, scan.OwnedDirectories);

    private static TLauncherScanResult ScanRoots(IEnumerable<string> roots, IEnumerable<TLauncherInstallation> installations,
        IEnumerable<string> initialWarnings, CancellationToken cancellationToken, IEnumerable<string>? knownDirectories = null)
    {
        string[] normalized = roots.Where(IsLocalRoot).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var files = new Dictionary<string, TLauncherFile>(StringComparer.OrdinalIgnoreCase);
        var protectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>(initialWarnings);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int inspected = 0;
        foreach (string root in normalized) Visit(root, 0);
        var known = (knownDirectories ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string root in normalized.Where(IsDedicatedDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(root) || IsProtectedPath(root) || !IsRegularPath(root)) continue;
            bool confirmed = files.Values.Any(f => IsWithin(f.Path, root)) || known.Contains(root) || installations.Any(i =>
                i.Directory != null && IsLocalRoot(i.Directory) && Path.GetFullPath(i.Directory).Equals(root, StringComparison.OrdinalIgnoreCase));
            if (!confirmed) continue;
            var entries = new List<string>();
            var localProtected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool legacy = false, complete = true;
            Collect(root, 0);
            foreach (string path in localProtected) protectedPaths.Add(path);
            if (legacy || !complete) continue;
            if (entries.Count == 0 && localProtected.Count > 0 && localProtected.All(IsGamePath)) continue;
            owned.Add(root);
            directories.Add(root);
            protectedPaths.RemoveWhere(path => IsWithin(path, root) && !IsGamePath(path) && !IsLegacyPath(path) && !localProtected.Contains(path));
            foreach (string path in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Directory.Exists(path)) { directories.Add(path); continue; }
                try { files[path] = Snapshot(path, "Папка TLauncher", cancellationToken); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { warnings.Add("Не удалось проверить файл: " + path); }
            }

            void Collect(string directory, int depth)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (depth > 48 || entries.Count > 100000) { complete = false; warnings.Add("Превышен объём проверки: " + root); return; }
                try
                {
                    foreach (string path in Directory.EnumerateFileSystemEntries(directory))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (IsLegacyPath(path)) { legacy = true; localProtected.Add(path); continue; }
                        if (IsGamePath(path)) { localProtected.Add(path); continue; }
                        if (!IsRegularPath(path)) { localProtected.Add(path); warnings.Add("Пропущена ссылка: " + path); continue; }
                        if (Directory.Exists(path)) { entries.Add(path); Collect(path, depth + 1); continue; }
                        try
                        {
                            if (IsLegacyFile(path)) { legacy = true; localProtected.Add(path); continue; }
                            entries.Add(path);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        { complete = false; localProtected.Add(path); warnings.Add("Не удалось проверить файл: " + path); }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { complete = false; warnings.Add("Не удалось прочитать папку: " + directory); }
            }
        }
        return new(files.Values.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase), installations, protectedPaths, warnings, normalized, owned, directories);

        void Visit(string directory, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(directory) || !visited.Add(directory)) return;
            if (IsProtectedPath(directory)) { protectedPaths.Add(directory); return; }
            if (!IsRegularPath(directory)) { warnings.Add("Пропущена ссылка или недоступная папка: " + directory); return; }
            try
            {
                foreach (string path in Directory.EnumerateFileSystemEntries(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (++inspected > 4000) { warnings.Add("Достигнут лимит проверки папки: " + directory); return; }
                    if (IsProtectedPath(path)) { protectedPaths.Add(path); continue; }
                    if (Directory.Exists(path))
                    {
                        if (depth < 3 && !SharedDirectories.Contains(Path.GetFileName(path))) Visit(path, depth + 1);
                        continue;
                    }
                    if (!IsCandidate(Path.GetFileName(path))) continue;
                    if (!IsRegularPath(path)) { warnings.Add("Пропущена ссылка: " + path); continue; }
                    try
                    {
                        if (new FileInfo(path).Length > 256L * 1024 * 1024) { warnings.Add("Слишком большой файл: " + path); continue; }
                        string? reason = Identify(path);
                        if (reason == "legacy") { protectedPaths.Add(path); continue; }
                        if (reason == null) { warnings.Add("Принадлежность TLauncher не подтверждена: " + path); continue; }
                        files[path] = Snapshot(path, reason, cancellationToken);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                    { warnings.Add("Не удалось проверить файл: " + path); }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { warnings.Add("Не удалось прочитать папку: " + directory); }
        }
    }

    private static bool IsCandidate(string name) =>
        Regex.IsMatch(name, @"^(tlauncher([ -][\d][\w. -]*)?|tl-uninstall)\.(exe|jar)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
        name.Equals("tlauncher-2.0.properties", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("TlauncherProfiles.json", StringComparison.OrdinalIgnoreCase);

    private static bool IsDedicatedDirectory(string path) => Regex.IsMatch(Path.GetFileName(Path.TrimEndingDirectorySeparator(path)),
        @"^\.?tlauncher(?:[ -]?[\d][\w. -]*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static bool IsGamePath(string path) => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(GameDirectories.Contains);

    internal static bool IsLegacyFile(string path)
    {
        string name = Path.GetFileName(path);
        if (name.Equals("tlauncher.cfg", StringComparison.OrdinalIgnoreCase) || name.Equals("tlauncher.properties", StringComparison.OrdinalIgnoreCase)) return true;
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".exe")
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return IsLegacyIdentity(string.Join(' ', info.ProductName, info.FileDescription, info.CompanyName, info.Comments));
        }
        if (extension == ".jar")
        {
            try { return Identify(path) == "legacy"; }
            catch (InvalidDataException) { return false; }
        }
        return extension is ".properties" or ".cfg" or ".json" or ".ini" && new FileInfo(path).Length <= 1024 * 1024 && IsLegacyIdentity(File.ReadAllText(path));
    }

    internal static TLauncherFile Snapshot(string path, string reason, CancellationToken cancellationToken, bool shortcut = false)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[65536]; int count;
        while ((count = stream.Read(buffer)) > 0) { cancellationToken.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, count); }
        return new(path, reason, stream.Length, Convert.ToHexString(hash.GetHashAndReset()), shortcut);
    }

    internal static string? Identify(string path)
    {
        if (IsLegacyPath(path) || !IsRegularPath(path)) return "legacy";
        string extension = Path.GetExtension(path);
        if (extension.Equals(".jar", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = ZipFile.OpenRead(path);
            if (zip.Entries.Count > 50000) return null;
            if (zip.Entries.Any(e => e.FullName.StartsWith("net/legacylauncher/", StringComparison.OrdinalIgnoreCase) ||
                e.FullName.StartsWith("org/tlauncher/legacy/", StringComparison.OrdinalIgnoreCase))) return "legacy";
            string manifest = "";
            if (zip.GetEntry("META-INF/MANIFEST.MF") is { } entry)
            {
                using var reader = new StreamReader(entry.Open());
                char[] buffer = new char[65537]; int read = reader.ReadBlock(buffer, 0, buffer.Length);
                if (read == buffer.Length) return null;
                manifest = new(buffer, 0, read);
            }
            if (IsLegacyIdentity(manifest)) return "legacy";
            return zip.GetEntry("org/tlauncher/tlauncher/rmo/TLauncher.class") != null &&
                zip.GetEntry("org/tlauncher/modpack/domain/client/ModpackDTO.class") != null ? "Классы TLauncher 2.x в JAR" : null;
        }
        if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            string identity = string.Join(' ', info.ProductName, info.FileDescription, info.CompanyName, info.Comments);
            if (IsLegacyIdentity(identity)) return "legacy";
            return IsTLauncherIdentity(info.ProductName ?? "", info.CompanyName ?? "", identity) ? "Метаданные приложения TLauncher" : null;
        }
        if (new FileInfo(path).Length > 1024 * 1024) return null;
        string text = File.ReadAllText(path);
        if (IsLegacyIdentity(text)) return "legacy";
        return HasTLauncherDomain(text) ? "Настройки с адресом сервиса TLauncher" : null;
    }

    internal static bool IsTLauncherIdentity(string name, string publisher, string details)
    {
        if (IsLegacyIdentity(name + " " + publisher + " " + details)) return false;
        return HasTLauncherDomain(details) || Regex.IsMatch(name.Trim(), @"^TLauncher(\s+[\d.]+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) &&
            Regex.IsMatch(publisher.Trim(), @"^(TLauncher( Inc\.?)?|tlauncher\.org)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool HasTLauncherDomain(string text) => Regex.IsMatch(text, @"(?<![\w.-])(?:[\w-]+\.)?tlauncher\.(org|ru)(?![\w.-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    internal static bool IsLegacyIdentity(string text) => Regex.IsMatch(text,
        @"tlegacy|(?:tl|tlauncher)[\s._-]*legacy|legacy[\s._-]*launcher|net[./]legacylauncher|org[./]tlauncher[./]legacy|\b(?:llaun|lln4|tlaun)\.(?:ch|ru)\b|(?:--brand\s+|brand[""']?\s*[:=]\s*[""']?)legacy",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    internal static bool IsLegacyPath(string path) => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        .Any(p => p.Equals("legacy", StringComparison.OrdinalIgnoreCase) || IsLegacyIdentity(p));
    internal static bool IsProtectedPath(string path) => IsLegacyPath(path) || path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(SharedDirectories.Contains);

    internal static bool IsRegularPath(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            if (full.StartsWith(@"\\", StringComparison.Ordinal) || full[Path.GetPathRoot(full)!.Length..].Contains(':')) return false;
            for (string? current = full; current != null; current = Path.GetDirectoryName(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    internal static bool IsWithin(string path, string root) => Path.GetFullPath(path).StartsWith(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    internal static bool IsLocalRoot(string path)
    {
        try { return !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) && !path.StartsWith(@"\\", StringComparison.Ordinal) &&
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) != Path.TrimEndingDirectorySeparator(Path.GetPathRoot(Path.GetFullPath(path))!); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return false; }
    }

    [SupportedOSPlatform("windows")]
    private static List<TLauncherInstallation> ReadInstallations(List<string> warnings)
    {
        var result = new List<TLauncherInstallation>();
        const string uninstall = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var key = root.OpenSubKey(uninstall);
                if (key == null) continue;
                foreach (string name in key.GetSubKeyNames())
                {
                    using var entry = key.OpenSubKey(name);
                    string display = entry?.GetValue("DisplayName") as string ?? "";
                    string publisher = entry?.GetValue("Publisher") as string ?? "";
                    if (IsLegacyPath(entry?.GetValue("InstallLocation") as string ?? "")) continue;
                    string details = string.Join(" ", display, publisher, name, entry?.GetValue("URLInfoAbout"), entry?.GetValue("InstallLocation"), entry?.GetValue("UninstallString"));
                    if (!IsTLauncherIdentity(display, publisher, details)) continue;
                    result.Add(new(display, $"{hive}\\{uninstall}\\{name}", entry?.GetValue("InstallLocation") as string, hive, view, uninstall + "\\" + name));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { warnings.Add($"Не удалось прочитать список программ: {hive}, {view}"); }
        }
        return result.DistinctBy(i => i.RegistryPath + "|" + i.View, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
