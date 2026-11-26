using System.Text.Json;
using MechanicaLauncher.Core.Instances;
using MechanicaLauncher.Core.IO;
using MechanicaLauncher.Core.Models;

namespace MechanicaLauncher.Core.Game;

public sealed record PreparedGame(VersionMeta Meta, string JavaPath, bool Modded);

// Downloads everything an instance needs to start: Java, the vanilla client, libraries, assets and the mod loader.
// Shared by Play and by the background preparation that starts right after an instance is created.
public static class GamePreparer
{
    public static async Task<PreparedGame> PrepareAsync(GameInstance instance, InstanceManager instances,
        Action<string, double> report, CancellationToken token)
    {
        var gameDir = instances.GetGameDir(instance.Id);
        var versions = new VersionManager(instances.SharedDir);
        var vanilla = await versions.GetVersionMetaAsync(instance.McVersion, token);
        var component = vanilla.JavaVersion?.Component ?? "jre-legacy";
        int requiredJava = vanilla.JavaVersion?.MajorVersion ?? 8;
        var downloader = new AssetDownloader(instances.SharedDir, gameDir);
        downloader.ProgressChanged += report;

        // Java and the game files come from different servers: fetch them at the same time.
        // Java status is shown only once the game files are done, so the two don't fight over one status line.
        var vanillaTask = Task.Run(() => downloader.DownloadVersionAsync(vanilla, token), token);
        var javaTask = ResolveJavaAsync(instance, instances.SharedDir, component, requiredJava,
            (status, progress) => { if (vanillaTask.IsCompleted) report(status, progress); }, token);
        try { await Task.WhenAll(javaTask, vanillaTask); }
        catch when (javaTask.IsFaulted || vanillaTask.IsFaulted)
        {
            // Surface the first real failure rather than an AggregateException.
            await (javaTask.IsFaulted ? javaTask : vanillaTask);
            throw;
        }
        var java = await javaTask;

        bool modded = instance.Loader != LoaderType.None;
        if (!modded) return new PreparedGame(vanilla, java, false);

        if (string.IsNullOrWhiteSpace(instance.LoaderVersion)) throw new InvalidOperationException("No mod loader version selected.");
        string versionId = instance.GetEffectiveVersionId();
        var versionDir = Path.Combine(gameDir, "versions", versionId);
        if (!File.Exists(Path.Combine(versionDir, versionId + ".json")) || !File.Exists(Path.Combine(versionDir, ".complete")))
        {
            if (!await LoaderProfileCache.TryReuseAsync(instance, instances, downloader, token))
            {
                await InstallLoaderAsync(instance, instances.SharedDir, gameDir, java, report, token);
                await LoaderProfileCache.StoreAsync(instances.SharedDir, gameDir, versionId, token);
            }
        }
        var meta = await versions.GetMergedMetaAsync(versionId, gameDir, token);
        await Task.Run(() => downloader.DownloadVersionAsync(new VersionMeta { Id = instance.McVersion, Libraries = meta.Libraries }, token), token);
        return new PreparedGame(meta, java, true);
    }

    private static async Task<string> ResolveJavaAsync(GameInstance instance, string sharedDir, string component, int requiredJava,
        Action<string, double> report, CancellationToken token)
    {
        var java = !string.IsNullOrEmpty(instance.JavaPath) ? instance.JavaPath : JavaFinder.FindJava(component, requiredJava);
        java ??= await JavaFinder.DownloadJavaAsync(component, sharedDir, status => report(status, -1), token);
        if (java == null) throw new InvalidOperationException("Java download failed.");
        JavaFinder.ValidateJava(java, requiredJava);
        return java;
    }

    private static async Task InstallLoaderAsync(GameInstance instance, string sharedDir, string gameDir, string java,
        Action<string, double> report, CancellationToken token)
    {
        string loaderVersion = instance.LoaderVersion!;
        switch (instance.Loader)
        {
            case LoaderType.Fabric:
                var fabric = new FabricInstaller(sharedDir, gameDir); fabric.ProgressChanged += report;
                await fabric.InstallAsync(instance.McVersion, loaderVersion, token); break;
            case LoaderType.Quilt:
                var quilt = new QuiltInstaller(sharedDir, gameDir); quilt.ProgressChanged += report;
                await quilt.InstallAsync(instance.McVersion, loaderVersion, token); break;
            case LoaderType.Forge:
                var forge = new ForgeInstaller(sharedDir, gameDir); forge.ProgressChanged += report;
                await forge.InstallAsync(instance.McVersion, loaderVersion, java, token); break;
            case LoaderType.NeoForge:
                var neo = new NeoForgeInstaller(sharedDir, gameDir); neo.ProgressChanged += report;
                await neo.InstallAsync(instance.McVersion, loaderVersion, java, token); break;
        }
    }
}

// A loader profile (Fabric/Quilt JSON, or the result of a Forge/NeoForge installer run) is the same for every
// instance with the same loader version. Forge/NeoForge installers write their output into the shared libraries,
// so once one instance installed a loader, others can reuse its profile instead of re-running the installer,
// which used to take a minute or more per new instance.
public static class LoaderProfileCache
{
    private static string StorePath(string sharedDir, string versionId) =>
        FileDownloader.GetPath(sharedDir, $"loader-profiles/{versionId}.json");

    public static async Task StoreAsync(string sharedDir, string gameDir, string versionId, CancellationToken token)
    {
        var source = FileDownloader.GetPath(gameDir, $"versions/{versionId}/{versionId}.json");
        if (!File.Exists(source)) return;
        await AtomicFile.WriteTextAsync(StorePath(sharedDir, versionId), await File.ReadAllTextAsync(source, token), token);
    }

    public static async Task<bool> TryReuseAsync(GameInstance instance, InstanceManager instances, AssetDownloader downloader, CancellationToken token)
    {
        string versionId = instance.GetEffectiveVersionId();
        var gameDir = instances.GetGameDir(instance.Id);
        foreach (var candidate in Candidates(instance, instances, versionId))
        {
            token.ThrowIfCancellationRequested();
            string json;
            VersionMeta? profile;
            try
            {
                json = await File.ReadAllTextAsync(candidate, token);
                profile = JsonSerializer.Deserialize<VersionMeta>(json);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { continue; }
            if (profile == null || string.IsNullOrEmpty(profile.MainClass) || profile.InheritsFrom != instance.McVersion) continue;
            try
            {
                // Verifies every loader library; ones produced by an installer have no URL and fail if missing.
                await downloader.DownloadVersionAsync(new VersionMeta { Id = instance.McVersion, Libraries = profile.Libraries }, token);
            }
            catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException) { continue; }
            if (RequiredLibrary(instance) is { } required &&
                !await FileDownloader.IsValidAsync(FileDownloader.GetPath(instances.SharedLibrariesDir, required), cancellationToken: token))
                continue;

            var versionDir = FileDownloader.GetPath(gameDir, $"versions/{versionId}");
            await AtomicFile.WriteTextAsync(Path.Combine(versionDir, $"{versionId}.json"), json, token);
            await AtomicFile.WriteTextAsync(Path.Combine(versionDir, ".complete"), "1", token);
            if (!candidate.Equals(StorePath(instances.SharedDir, versionId), StringComparison.OrdinalIgnoreCase))
                await StoreAsync(instances.SharedDir, gameDir, versionId, token);
            return true;
        }
        return false;
    }

    private static IEnumerable<string> Candidates(GameInstance instance, InstanceManager instances, string versionId)
    {
        var stored = StorePath(instances.SharedDir, versionId);
        if (File.Exists(stored)) yield return stored;
        if (!Directory.Exists(instances.InstancesDir)) yield break;
        foreach (var dir in Directory.EnumerateDirectories(instances.InstancesDir))
        {
            if (Path.GetFileName(dir).Equals(instance.Id, StringComparison.OrdinalIgnoreCase)) continue;
            var versionDir = Path.Combine(dir, ".minecraft", "versions", versionId);
            var json = Path.Combine(versionDir, versionId + ".json");
            if (File.Exists(json) && File.Exists(Path.Combine(versionDir, ".complete"))) yield return json;
        }
    }

    private static string? RequiredLibrary(GameInstance instance) =>
        instance.Loader == LoaderType.NeoForge && !string.IsNullOrEmpty(instance.LoaderVersion)
            ? NeoForgeInstaller.GetInstallArtifact(instance.McVersion, instance.LoaderVersion).RequiredLibrary
            : null;
}
