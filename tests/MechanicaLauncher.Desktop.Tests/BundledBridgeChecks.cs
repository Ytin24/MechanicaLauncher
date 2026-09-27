using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MechanicaLauncher.Core.Instances;
using MechanicaLauncher.Core.Profiles;
using MechanicaLauncher.Desktop;

internal static partial class Program
{
    private static void BundledBridgeChecks(Pump context)
    {
        string? previousData = Environment.GetEnvironmentVariable("MECHANICA_DATA_DIR");
        string data = Path.Combine(output, "bundled-bridge-data");
        Environment.SetEnvironmentVariable("MECHANICA_DATA_DIR", data);
        try
        {
            using var bundle = new BridgeBundleFixture();
            var fabric = bundle.Add("1.21.1", LoaderType.Fabric, "0.19.3", "1.0.0");
            var olderMinecraft = bundle.Add("1.20.1", LoaderType.Fabric, "0.19.3", "1.0.1");
            var neoForge = bundle.Add("1.21.1", LoaderType.NeoForge, "21.1.1", "1.0.2");
            var updated = bundle.Add("1.21.1", LoaderType.Fabric, "0.19.3", "1.1.0");
            bundle.Manifest(olderMinecraft, neoForge, fabric);
            var instances = new InstanceManager(data);
            using var sessions = new GameSessions(new LauncherSettings { DiscordRpc = false }, instances);
            int number = 0;
            GameInstance Create(string minecraft = "1.21.1", LoaderType loader = LoaderType.Fabric, string version = "0.19.3", bool enabled = true)
            {
                var instance = instances.CreateInstance("Bundle fixture " + ++number, minecraft, loader, version);
                instance.UseServerModSync = enabled; instances.SaveInstance(instance);
                File.WriteAllText(Path.Combine(instances.GetGameDir(instance.Id), "mods", "personal.txt"), "personal mod sentinel");
                return instance;
            }
            string Game(GameInstance instance) => instances.GetGameDir(instance.Id);
            string Target(GameInstance instance) => Path.Combine(Game(instance), "mods", "mechanica-server-sync-1.0.0.jar");
            string Receipt(GameInstance instance) => Path.Combine(Game(instance), ".mechanica", "bridge-sha512.txt");
            void OptIn(GameInstance instance, bool enabled) { instance.UseServerModSync = enabled; instances.SaveInstance(instance); }
            Task Install(GameInstance instance, CancellationToken token = default) =>
                (Task)BridgeLifecycleMethod("InstallBundledBridgeAsync").Invoke(sessions, [instance, Game(instance), token])!;
            void Wait(Task task, string operation)
            {
                var timer = Stopwatch.StartNew();
                while (!task.IsCompleted)
                {
                    if (timer.Elapsed.TotalSeconds > 10) throw new TimeoutException("Bundled bridge: " + operation);
                    context.Drain(); Thread.Sleep(1);
                }
                context.Drain();
            }
            void Await(Task task, string operation) { Wait(task, operation); task.GetAwaiter().GetResult(); }
            void UnchangedFailure<T>(GameInstance instance, string operation, CancellationToken token = default) where T : Exception
            {
                string[] before = BridgeFixtureTree(Game(instance));
                var task = Install(instance, token);
                Wait(task, operation);
                Exception? failure = null;
                try { task.GetAwaiter().GetResult(); } catch (Exception ex) { failure = ex; }
                Check(failure is T, operation + " reports " + typeof(T).Name + ": " + failure?.GetType().Name);
                Check(before.SequenceEqual(BridgeFixtureTree(Game(instance))), operation + " leaves every existing file and receipt unchanged");
            }
            void Installed(GameInstance instance, BridgeFixturePackage package, string operation)
            {
                Check(File.Exists(Target(instance)) && BridgeFixtureHash(File.ReadAllBytes(Target(instance))).Equals(package.Sha512, StringComparison.OrdinalIgnoreCase),
                    operation + " installs the selected package bytes");
                Check(File.Exists(Receipt(instance)) && File.ReadAllText(Receipt(instance)).Trim().Equals(package.Sha512, StringComparison.OrdinalIgnoreCase) &&
                    !File.Exists(Target(instance) + ".disabled"), operation + " records ownership of only the active package");
                Check(File.ReadAllText(Path.Combine(Game(instance), "mods", "personal.txt")) == "personal mod sentinel", operation + " preserves personal content");
            }

            var defaultOff = Create(enabled: false);
            Check(!instances.GetInstance(defaultOff.Id)!.UseServerModSync, "a persisted opt-out remains disabled");
            string[] offBefore = BridgeFixtureTree(Game(defaultOff));
            bundle.RemoveManifest();
            Await(Install(defaultOff), "skipping a missing catalog for opt-out");
            Check(offBefore.SequenceEqual(BridgeFixtureTree(Game(defaultOff))), "opt-out needs no bundle catalog and creates no bridge or receipt");
            byte[] personalJar = Encoding.UTF8.GetBytes("an unrelated file using the conventional bridge name");
            File.WriteAllBytes(Target(defaultOff), personalJar);
            offBefore = BridgeFixtureTree(Game(defaultOff));
            Await(Install(defaultOff), "preserving an unowned bridge filename");
            Check(offBefore.SequenceEqual(BridgeFixtureTree(Game(defaultOff))), "disabling synchronization never moves or claims an unowned JAR");
            bundle.Manifest(olderMinecraft, neoForge, fabric);
            OptIn(defaultOff, true);
            UnchangedFailure<InvalidOperationException>(defaultOff, "enabling with a different unowned bridge filename");

            foreach (var package in new[] { fabric, olderMinecraft, neoForge })
            {
                var instance = Create(package.Minecraft, Enum.Parse<LoaderType>(package.Loader), package.MinimumLoaderVersion);
                Await(Install(instance), "selecting " + package.Loader + " " + package.Minecraft);
                Installed(instance, package, "exact Minecraft and loader selection");
                Check(!File.Exists(Path.Combine(Game(instance), "mods", package.FileName)), "package source names do not leak into the installed mod directory");
                File.SetLastWriteTimeUtc(Target(instance), new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));
                DateTime stamp = File.GetLastWriteTimeUtc(Target(instance));
                Await(Install(instance), "checking an already current owned bridge");
                Check(File.GetLastWriteTimeUtc(Target(instance)) == stamp, "an already current bridge is not rewritten");
            }

            var managed = Create();
            Await(Install(managed), "installing the managed fixture");
            byte[] ownedBytes = File.ReadAllBytes(Target(managed));
            byte[] ownedReceipt = File.ReadAllBytes(Receipt(managed));
            OptIn(managed, false);
            sessions.Events.SetActive(new() { Ui = new() { AllowModInstall = false, AllowModToggle = true } });
            Await(Install(managed), "disabling while installation is locked");
            Check(!File.Exists(Target(managed)) && File.ReadAllBytes(Target(managed) + ".disabled").SequenceEqual(ownedBytes) &&
                File.ReadAllBytes(Receipt(managed)).SequenceEqual(ownedReceipt), "opt-out disables only the owned JAR and preserves its receipt even when installation is locked");
            Check(File.ReadAllText(Path.Combine(Game(managed), "mods", "personal.txt")) == "personal mod sentinel", "disabling the managed bridge preserves personal mods");
            OptIn(managed, true);
            UnchangedFailure<InvalidOperationException>(managed, "installation lock prevents restoring a disabled bridge");
            sessions.Events.Clear();
            Await(Install(managed), "restoring the owned disabled fixture");
            Installed(managed, fabric, "re-enabling the same package");
            OptIn(managed, false);
            sessions.Events.SetActive(new() { Ui = new() { AllowModToggle = false } });
            UnchangedFailure<InvalidOperationException>(managed, "toggle lock prevents disabling a managed bridge");
            sessions.Events.Clear();
            Await(Install(managed), "disabling before a package update");
            bundle.Manifest(olderMinecraft, neoForge, updated);
            OptIn(managed, true);
            Await(Install(managed), "restoring and updating an owned disabled package");
            Installed(managed, updated, "re-enabling after a bundle update");
            bundle.Manifest(olderMinecraft, neoForge, fabric);

            var activeUpdate = Create();
            Await(Install(activeUpdate), "installing before an active update");
            bundle.Manifest(olderMinecraft, neoForge, updated);
            Await(Install(activeUpdate), "updating an active owned package");
            Installed(activeUpdate, updated, "active package update");
            bundle.Manifest(olderMinecraft, neoForge, fabric);

            var changed = Create();
            Await(Install(changed), "installing before an external edit");
            File.WriteAllBytes(Target(changed), personalJar);
            UnchangedFailure<InvalidOperationException>(changed, "a modified owned JAR cannot be overwritten");
            OptIn(changed, false);
            UnchangedFailure<InvalidOperationException>(changed, "a modified owned JAR cannot be disabled as launcher-owned");
            var changedDisabled = Create();
            Await(Install(changedDisabled), "installing before a disabled-file edit");
            OptIn(changedDisabled, false); Await(Install(changedDisabled), "disabling before an external edit");
            File.WriteAllBytes(Target(changedDisabled) + ".disabled", personalJar);
            OptIn(changedDisabled, true);
            UnchangedFailure<InvalidOperationException>(changedDisabled, "an edited disabled JAR cannot be restored or overwritten");

            var collision = Create();
            Await(Install(collision), "installing before a disabled-name collision");
            File.WriteAllBytes(Target(collision) + ".disabled", personalJar);
            OptIn(collision, false);
            UnchangedFailure<InvalidOperationException>(collision, "disabling never overwrites an existing disabled file");

            sessions.Events.SetActive(new() { Ui = new() { AllowModInstall = false } });
            var locked = Create();
            UnchangedFailure<InvalidOperationException>(locked, "event installation lock prevents a fresh install");
            var identicalPersonal = Create();
            File.WriteAllBytes(Target(identicalPersonal), bundle.Bytes(fabric));
            string[] personalBefore = BridgeFixtureTree(Game(identicalPersonal));
            Await(Install(identicalPersonal), "allowing an already installed personal bridge under installation lock");
            Check(personalBefore.SequenceEqual(BridgeFixtureTree(Game(identicalPersonal))) && !File.Exists(Receipt(identicalPersonal)),
                "an identical personal bridge remains unowned and launchable while installation is locked");
            sessions.Events.Clear();

            UnchangedFailure<InvalidOperationException>(Create("1.21.9"), "an unsupported Minecraft version");
            UnchangedFailure<InvalidOperationException>(Create(loader: LoaderType.Quilt), "an unsupported loader for a supported Minecraft version");
            UnchangedFailure<InvalidOperationException>(Create(loader: LoaderType.None), "an explicitly enabled vanilla instance");
            UnchangedFailure<InvalidOperationException>(Create(version: "0.19.2"), "a loader below the manifest minimum");
            UnchangedFailure<InvalidOperationException>(Create(version: "unrecognized"), "an invalid loader version");
            var suffixVersion = Create(version: "0.19.3-test");
            Await(Install(suffixVersion), "accepting the supported numeric loader version with a suffix");
            Installed(suffixVersion, fabric, "loader version suffix");

            var invalidBundle = Create();
            bundle.WritePackage(fabric, personalJar);
            UnchangedFailure<InvalidDataException>(invalidBundle, "a bundle whose content does not match its SHA512");
            bundle.Manifest(fabric with { FileName = "missing-test-" + Guid.NewGuid().ToString("N") + ".jar" });
            UnchangedFailure<FileNotFoundException>(invalidBundle, "a manifest with a missing package");
            bundle.Manifest(fabric with { FileName = "../outside.jar" });
            UnchangedFailure<InvalidDataException>(invalidBundle, "a package filename escaping its bundle directory");
            bundle.RawManifest("{");
            UnchangedFailure<JsonException>(invalidBundle, "a malformed bundle manifest");
            bundle.RemoveManifest();
            UnchangedFailure<FileNotFoundException>(invalidBundle, "an enabled instance with no bundle manifest");
            bundle.Manifest(updated);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            UnchangedFailure<OperationCanceledException>(Create(), "a cancelled fresh installation", cancelled.Token);
        }
        finally { Environment.SetEnvironmentVariable("MECHANICA_DATA_DIR", previousData); }
    }

    private static string BridgeFixtureHash(byte[] bytes) => Convert.ToHexString(SHA512.HashData(bytes));

    private static string[] BridgeFixtureTree(string directory) => Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories)
        .Select(path => Path.GetRelativePath(directory, path) + (Directory.Exists(path) ? "|directory" :
            "|" + BridgeFixtureHash(File.ReadAllBytes(path)) + "|" + File.GetLastWriteTimeUtc(path).Ticks))
        .OrderBy(path => path, StringComparer.Ordinal).ToArray();

    private sealed record BridgeFixturePackage(string Minecraft, string Loader, string MinimumLoaderVersion, string FileName, string Sha512);

    private sealed class BridgeBundleFixture : IDisposable
    {
        private readonly string directory = Path.Combine(AppContext.BaseDirectory, "bridges");
        private readonly Dictionary<string, byte[]?> originals = new(StringComparer.OrdinalIgnoreCase);
        private readonly bool directoryExisted;

        public BridgeBundleFixture()
        {
            directoryExisted = Directory.Exists(directory);
            Directory.CreateDirectory(directory);
        }

        public BridgeFixturePackage Add(string minecraft, LoaderType loader, string minimumLoader, string version)
        {
            string fileName = "mechanica-test-" + Guid.NewGuid().ToString("N") + ".jar";
            using var bytes = new MemoryStream();
            using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
            {
                string entry = loader == LoaderType.Fabric ? "fabric.mod.json" : loader == LoaderType.NeoForge ? "META-INF/neoforge.mods.toml" : "META-INF/mods.toml";
                using (var writer = new StreamWriter(zip.CreateEntry(entry).Open(), new UTF8Encoding(false)))
                    writer.Write(loader == LoaderType.Fabric
                        ? JsonSerializer.Serialize(new { schemaVersion = 1, id = "mechanica_server_sync", version, environment = "client", depends = new { minecraft, fabricloader = ">=" + minimumLoader } })
                        : "modLoader=\"javafml\"\nloaderVersion=\"[1,)\"\nlicense=\"MIT\"\n[[mods]]\nmodId=\"mechanica_server_sync\"\nversion=\"" + version + "\"\ndisplayName=\"Bridge fixture\"\n");
                using var marker = new StreamWriter(zip.CreateEntry("META-INF/bridge-fixture.txt").Open(), new UTF8Encoding(false));
                marker.Write(minecraft + " " + loader + " " + minimumLoader + " " + version);
            }
            byte[] content = bytes.ToArray();
            var package = new BridgeFixturePackage(minecraft, loader.ToString(), minimumLoader, fileName, BridgeFixtureHash(content).ToLowerInvariant());
            WritePackage(package, content);
            return package;
        }

        public byte[] Bytes(BridgeFixturePackage package) => File.ReadAllBytes(Path.Combine(directory, package.FileName));
        public void WritePackage(BridgeFixturePackage package, byte[] bytes) => Write(package.FileName, bytes);
        public void Manifest(params BridgeFixturePackage[] packages) => Write("manifest.json", JsonSerializer.SerializeToUtf8Bytes(packages));
        public void RawManifest(string json) => Write("manifest.json", Encoding.UTF8.GetBytes(json));
        public void RemoveManifest()
        {
            string path = Remember("manifest.json");
            if (File.Exists(path)) File.Delete(path);
        }
        private void Write(string name, byte[] bytes) => File.WriteAllBytes(Remember(name), bytes);
        private string Remember(string name)
        {
            if (name != Path.GetFileName(name) || name.IndexOfAny(['/', '\\', ':']) >= 0) throw new InvalidOperationException("Invalid fixture filename.");
            string path = Path.Combine(directory, name);
            if (!originals.ContainsKey(path)) originals.Add(path, File.Exists(path) ? File.ReadAllBytes(path) : null);
            return path;
        }
        public void Dispose()
        {
            foreach (var file in originals)
            {
                if (file.Value != null) File.WriteAllBytes(file.Key, file.Value);
                else if (File.Exists(file.Key)) File.Delete(file.Key);
            }
            if (!directoryExisted && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
    }
}
