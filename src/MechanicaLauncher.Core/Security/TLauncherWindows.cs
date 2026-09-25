using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace MechanicaLauncher.Core.Security;

public sealed record TLauncherProcess(int Id, long StartedUtcTicks, string Executable, string LauncherFile);

public sealed class TLauncherRegistryEntry
{
    public string Path => $"{Hive}\\{Key}" + (Value == null ? "" : "\\" + Value);
    public string Id => Path + "|" + View + (userSid == null ? "" : "|" + userSid);
    private readonly string? userSid;
    public string Name { get; }
    public string Snapshot { get; }
    internal RegistryHive Hive { get; }
    internal RegistryView View { get; }
    internal string Key { get; }
    internal string? Value { get; }
    internal TLauncherRegistryEntry(RegistryHive hive, RegistryView view, string key, string? value, string name, string snapshot)
    {
        Hive = hive; View = view; Key = key; Value = value; Name = name; Snapshot = snapshot;
        if (hive == RegistryHive.CurrentUser && OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            userSid = identity.User?.Value;
        }
    }
}

[SupportedOSPlatform("windows")]
internal static class TLauncherWindows
{
    internal sealed record RegistryArea(RegistryHive Hive, RegistryView View, string Uninstall, string Run, string RunOnce, string Settings);
    private sealed record RegistryValue(string Name, RegistryValueKind Kind, object Value);
    private sealed record RegistryTree(RegistryValue[] Values, SortedDictionary<string, RegistryTree> Children);

    internal static bool RequiresElevation(TLauncherScanResult scan)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return false;
        foreach (var entry in scan.RegistryEntries)
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(entry.Hive, entry.View);
                using var key = root.OpenSubKey(entry.Key, writable: true);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException) { return true; }
        }
        foreach (string path in scan.Files.Select(file => file.Path).Concat(scan.Directories))
        {
            if (!TLauncherDetector.IsRegularPath(path)) continue;
            using var handle = CreateFileW(path, 0x10000, 7, 0, 3, 0x02000000, 0);
            if (handle.IsInvalid && Marshal.GetLastWin32Error() == 5) return true;
        }
        return false;
    }

    internal static TLauncherScanResult AddArtifacts(TLauncherScanResult scan, IEnumerable<RegistryArea>? areas = null,
        IEnumerable<string>? shortcutFolders = null, CancellationToken cancellationToken = default)
    {
        var warnings = scan.Warnings.ToList();
        var registry = new List<TLauncherRegistryEntry>();
        var files = scan.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var directories = scan.Directories.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var processes = new List<TLauncherProcess>();
        var folders = shortcutFolders ?? new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory,
            Environment.SpecialFolder.Programs, Environment.SpecialFolder.CommonPrograms, Environment.SpecialFolder.Startup, Environment.SpecialFolder.CommonStartup }
            .Select(Environment.GetFolderPath);
        var targets = LauncherTargets(scan.Files);
        foreach (var area in areas ?? DefaultAreas())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var root = RegistryKey.OpenBaseKey(area.Hive, area.View);
                using (var uninstall = root.OpenSubKey(area.Uninstall))
                    foreach (string child in uninstall?.GetSubKeyNames() ?? [])
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        using var key = uninstall!.OpenSubKey(child);
                        if (key == null) continue;
                        string title = key.GetValue("DisplayName") as string ?? "";
                        string publisher = key.GetValue("Publisher") as string ?? "";
                        string location = key.GetValue("InstallLocation") as string ?? "";
                        if (TLauncherDetector.IsLegacyPath(location) || scan.ProtectedPaths.Any(p =>
                            TLauncherDetector.IsLocalRoot(location) && TLauncherDetector.IsWithin(p, location) &&
                            (TLauncherDetector.IsLegacyPath(p) || p.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))) && !targets.Any(p =>
                            TLauncherDetector.IsLocalRoot(location) && TLauncherDetector.IsWithin(p, location))) continue;
                        string details = string.Join(' ', title, publisher, key.GetValue("URLInfoAbout"), key.GetValue("InstallLocation"));
                        if (!TLauncherDetector.IsTLauncherIdentity(title, publisher, details)) continue;
                        string snapshot = Serialize(key, null);
                        if (!IsLegacySnapshot(snapshot)) registry.Add(new(area.Hive, area.View, area.Uninstall + "\\" + child, null, "TLauncher · приложение", snapshot));
                    }
                foreach (string startup in new[] { area.Run, area.RunOnce })
                {
                    using var key = root.OpenSubKey(startup);
                    foreach (string value in key?.GetValueNames() ?? [])
                    {
                        if (key!.GetValue(value) is not string command || TLauncherDetector.IsLegacyIdentity(value + " " + command) || !ReferencesLauncher(command, targets)) continue;
                        registry.Add(new(area.Hive, area.View, startup, value, "TLauncher · автозапуск", Serialize(key, value)));
                    }
                }
                using var settings = root.OpenSubKey(area.Settings);
                if (settings != null)
                {
                    string snapshot = Serialize(settings, null);
                    if (!IsLegacySnapshot(snapshot) && TLauncherDetector.IsTLauncherIdentity("", "", snapshot))
                        registry.Add(new(area.Hive, area.View, area.Settings, null, "TLauncher · настройки Windows", snapshot));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { warnings.Add("Не удалось проверить записи Windows: " + area.Hive); }
        }
        object? shell = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int inspected = 0;
            foreach (string folder in folders.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase)) Visit(folder, 0);
            void Visit(string folder, int depth)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (depth > 5 || !visited.Add(folder) || !TLauncherDetector.IsRegularPath(folder) || TLauncherDetector.IsLegacyPath(folder)) return;
                try
                {
                    foreach (string path in Directory.EnumerateFileSystemEntries(folder))
                    {
                        if (++inspected > 12000) return;
                        if (Directory.Exists(path)) { Visit(path, depth + 1); continue; }
                        if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || TLauncherDetector.IsLegacyPath(path) || !TLauncherDetector.IsRegularPath(path)) continue;
                        object? shortcut = null;
                        try
                        {
                            shortcut = ((dynamic)shell!).CreateShortcut(path);
                            string target = ((dynamic)shortcut).TargetPath;
                            string arguments = ((dynamic)shortcut).Arguments;
                            string description = ((dynamic)shortcut).Description;
                            if (TLauncherDetector.IsLegacyIdentity(target + " " + arguments + " " + description)) continue;
                            if (ReferencesLauncher('"' + target + "\" " + arguments, targets))
                            {
                                files[path] = TLauncherDetector.Snapshot(path, "Ярлык TLauncher", cancellationToken, shortcut: true);
                                string parent = Path.GetDirectoryName(path)!;
                                if (Path.GetFileName(parent).Equals("TLauncher", StringComparison.OrdinalIgnoreCase)) directories.Add(parent);
                            }
                        }
                        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException)
                        { warnings.Add("Не удалось проверить ярлык: " + path); }
                        finally { if (shortcut != null) Marshal.FinalReleaseComObject(shortcut); }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { warnings.Add("Не удалось прочитать ярлыки: " + folder); }
            }
        }
        catch (Exception ex) when (ex is COMException or TypeLoadException)
        { warnings.Add("Не удалось проверить ярлыки Windows."); }
        finally { if (shell != null) Marshal.FinalReleaseComObject(shell); }
        if (areas == null) processes.AddRange(ReadProcesses(targets));
        return new(files.Values, scan.Installations, scan.ProtectedPaths, warnings, scan.Roots, scan.OwnedDirectories, directories, registry, processes);
    }

    private static IEnumerable<RegistryArea> DefaultAreas()
    {
        const string parent = @"Software\Microsoft\Windows\CurrentVersion\";
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in hive == RegistryHive.CurrentUser ? new[] { RegistryView.Default } : new[] { RegistryView.Registry64, RegistryView.Registry32 })
            yield return new(hive, view, parent + "Uninstall", parent + "Run", parent + "RunOnce", @"Software\TLauncher");
    }

    private static HashSet<string> LauncherTargets(IEnumerable<TLauncherFile> files)
    {
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files.Where(f => Path.GetExtension(f.Path).Equals(".exe", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(f.Path).Equals(".jar", StringComparison.OrdinalIgnoreCase)))
        {
            try { if (TLauncherDetector.Identify(file.Path) is { } identity && identity != "legacy") targets.Add(file.Path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
        }
        return targets;
    }

    internal static bool ReferencesLauncher(string command, IReadOnlySet<string> targets)
    {
        if (TLauncherDetector.IsLegacyIdentity(command)) return false;
        string[] args = Arguments(Environment.ExpandEnvironmentVariables(command));
        if (args.Length == 0) return false;
        if (targets.Contains(args[0])) return true;
        string executable = Path.GetFileNameWithoutExtension(args[0]);
        if (!executable.Equals("java", StringComparison.OrdinalIgnoreCase) && !executable.Equals("javaw", StringComparison.OrdinalIgnoreCase)) return false;
        for (int i = 1; i < args.Length - 1; i++)
            if (args[i].Equals("-jar", StringComparison.Ordinal) && targets.Contains(args[i + 1])) return true;
        return args.Contains("org.tlauncher.tlauncher.rmo.TLauncher", StringComparer.Ordinal) && args.Any(arg => arg.Split(';').Any(targets.Contains));
    }

    private static string[] Arguments(string command)
    {
        nint buffer = CommandLineToArgvW(command, out int count);
        if (buffer == 0) return [];
        try { return Enumerable.Range(0, count).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer, i * IntPtr.Size)) ?? "").ToArray(); }
        finally { LocalFree(buffer); }
    }

    internal static string? CurrentSnapshot(TLauncherRegistryEntry entry)
    {
        using var root = RegistryKey.OpenBaseKey(entry.Hive, entry.View);
        using var key = root.OpenSubKey(entry.Key);
        if (key == null || entry.Value != null && !key.GetValueNames().Contains(entry.Value, StringComparer.OrdinalIgnoreCase)) return null;
        return Serialize(key, entry.Value);
    }

    internal static bool RemoveRegistryEntry(TLauncherRegistryEntry entry)
    {
        using var root = RegistryKey.OpenBaseKey(entry.Hive, entry.View);
        using var key = root.OpenSubKey(entry.Key, writable: true);
        if (key == null || entry.Value != null && !key.GetValueNames().Contains(entry.Value, StringComparer.OrdinalIgnoreCase)) return true;
        if (Serialize(key, entry.Value) != entry.Snapshot) return false;
        if (entry.Value != null) key.DeleteValue(entry.Value, throwOnMissingValue: false);
        else root.DeleteSubKeyTree(entry.Key, throwOnMissingSubKey: false);
        return true;
    }

    internal static object RegistryBackup(TLauncherRegistryEntry entry) => new { hive = entry.Hive.ToString(), view = entry.View.ToString(), key = entry.Key, value = entry.Value, snapshot = JsonSerializer.Deserialize<JsonElement>(entry.Snapshot) };

    private static string Serialize(RegistryKey key, string? value)
    {
        int count = 0;
        return JsonSerializer.Serialize(value == null ? (object)Tree(key, 0) : ReadValue(key, value));
        RegistryTree Tree(RegistryKey node, int depth)
        {
            if (depth > 12 || ++count > 2048) throw new IOException("Слишком большая запись Windows.");
            var children = new SortedDictionary<string, RegistryTree>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in node.GetSubKeyNames().Order(StringComparer.OrdinalIgnoreCase))
            {
                using var child = node.OpenSubKey(name);
                if (child == null) throw new IOException("Запись Windows изменилась.");
                children.Add(name, Tree(child, depth + 1));
            }
            return new(node.GetValueNames().Order(StringComparer.OrdinalIgnoreCase).Select(name => ReadValue(node, name)).ToArray(), children);
        }
        RegistryValue ReadValue(RegistryKey node, string name)
        {
            if (++count > 2048) throw new IOException("Слишком большая запись Windows.");
            object data = node.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) ?? throw new IOException("Запись Windows изменилась.");
            if (data is byte[] bytes && bytes.Length > 1024 * 1024 || data is string text && text.Length > 1024 * 1024) throw new IOException("Слишком большая запись Windows.");
            return new(name, node.GetValueKind(name), data);
        }
    }

    private static bool IsLegacySnapshot(string snapshot)
    {
        if (TLauncherDetector.IsLegacyIdentity(snapshot)) return true;
        using var json = JsonDocument.Parse(snapshot);
        return Visit(json.RootElement);
        static bool Visit(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Array) return node.EnumerateArray().Any(Visit);
            if (node.ValueKind != JsonValueKind.Object) return false;
            if (node.TryGetProperty("Name", out var name) && node.TryGetProperty("Value", out var value) && value.ValueKind == JsonValueKind.String)
            {
                string text = value.GetString()!;
                if (TLauncherDetector.IsLocalRoot(text) && TLauncherDetector.IsLegacyPath(text) ||
                    name.GetString()!.EndsWith("brand", StringComparison.OrdinalIgnoreCase) && text.StartsWith("legacy", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return node.EnumerateObject().Any(p => Visit(p.Value));
        }
    }

    private static List<TLauncherProcess> ReadProcesses(IReadOnlySet<string> targets)
    {
        var result = new List<TLauncherProcess>();
        if (targets.Count == 0) return result;
        object? locator = null, services = null, items = null;
        try
        {
            locator = Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator", throwOnError: true)!);
            services = ((dynamic)locator!).ConnectServer(".", @"root\cimv2");
            items = ((dynamic)services).ExecQuery("SELECT ProcessId, ExecutablePath, CommandLine FROM Win32_Process WHERE Name='TLauncher.exe' OR Name='java.exe' OR Name='javaw.exe'");
            foreach (object item in (System.Collections.IEnumerable)items)
            {
                try
                {
                    string command = ((dynamic)item).CommandLine ?? "";
                    string executable = ((dynamic)item).ExecutablePath ?? "";
                    if (!ReferencesLauncher(command, targets)) continue;
                    string? launcher = Arguments(command).SelectMany(a => a.Split(';')).FirstOrDefault(targets.Contains);
                    if (launcher == null) continue;
                    using var process = Process.GetProcessById((int)(uint)((dynamic)item).ProcessId);
                    result.Add(new(process.Id, process.StartTime.ToUniversalTime().Ticks, executable, launcher));
                }
                catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception) { }
                finally { Marshal.FinalReleaseComObject(item); }
            }
        }
        catch (Exception ex) when (ex is COMException or TypeLoadException) { }
        finally
        {
            if (items != null) Marshal.FinalReleaseComObject(items);
            if (services != null) Marshal.FinalReleaseComObject(services);
            if (locator != null) Marshal.FinalReleaseComObject(locator);
        }
        return result;
    }

    internal static async Task StopProcess(TLauncherProcess approved, CancellationToken token)
    {
        Process process;
        try { process = Process.GetProcessById(approved.Id); }
        catch (ArgumentException) { return; }
        using (process)
        {
            if (process.HasExited) return;
            if (process.StartTime.ToUniversalTime().Ticks != approved.StartedUtcTicks ||
                !string.Equals(process.MainModule?.FileName, approved.Executable, StringComparison.OrdinalIgnoreCase)) throw new IOException("Процесс изменился.");
            if (process.CloseMainWindow())
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(token); wait.CancelAfter(TimeSpan.FromSeconds(2));
                try { await process.WaitForExitAsync(wait.Token).ConfigureAwait(false); return; }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            }
            token.ThrowIfCancellationRequested();
            if (!process.HasExited) process.Kill(entireProcessTree: false);
            await process.WaitForExitAsync(token).ConfigureAwait(false);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}
