using System.IO.Compression;
using System.Text;
using System.Text.Json;
using MechanicaLauncher.Core.Instances;
using MechanicaLauncher.Core.Models;
using MechanicaLauncher.Core.Mods;

internal static class ModLoaderCompatibilityTests
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check, string root)
    {
        await check("Maven dependency ranges preserve bounds unions and soft recommendations", () =>
        {
            foreach (var (version, range, expected) in new (string, string, bool?)[]
            {
                ("1.2", "[1.2]", true), ("1.2.0", "[1.2]", true), ("1.3", "[1.2]", false),
                ("1", "[1,2)", true), ("2", "[1,2)", false), ("1", "(1,2]", false), ("2", "(1,2]", true),
                ("0.9", "(,1.0],[1.2,)", true), ("1.1", "(,1.0],[1.2,)", false), ("1.2", "(,1.0],[1.2,)", true),
                ("1.0", "1.5", true), ("unversioned", "", true), ("26.3.0.23-beta", "[26.3.0.1-beta,)", true),
                ("1.0-beta2", "[1.0-beta1,1.0)", true), ("1.0-beta1", "[1.0,)", false), ("1.0-final", "[1.0]", true),
                ("1", "[2,1]", null), ("1", "[1,1)", null), ("1", "[1,2],", null), ("1", "[1,3],[2,4]", null),
                ("1", "[1", null), ("1.0-custom", "[1.0,)", null), ("1.0-beta.1", "[1.0-beta1]", null),
                ("custom", "[custom]", true), ("1", "[unknown,2]", null)
            })
                Require(ModCompatibilityChecker.MatchesMaven(version, range) == expected, version + " " + range);
            return Task.CompletedTask;
        });

        await check("TOML compatibility matrix validates exact Minecraft and loader generations", async () =>
        {
            foreach (var (loader, minecraft, version, builtin) in new[]
            {
                (LoaderType.Forge, "1.16.5", "36.2.42", "forge"), (LoaderType.Forge, "1.20.1", "47.4.0", "forge"),
                (LoaderType.NeoForge, "1.20.1", "47.1.106", "forge"), (LoaderType.NeoForge, "1.21.1", "21.1.219", "neoforge"),
                (LoaderType.NeoForge, "26.3", "26.3.0.23-beta", "neoforge")
            })
            {
                var f = new Fixture(root, "matrix-" + loader + minecraft, loader, minecraft, version);
                var schema = loader == LoaderType.NeoForge && minecraft != "1.20.1" ? LoaderType.NeoForge : LoaderType.Forge;
                f.Mod("main.jar", "main", "1.0", Dep("main", "minecraft", "[" + minecraft + "]", schema)
                    + Dep("main", builtin, "[" + version + "]", schema));
                Require(!(await f.Check()).Issues.Any(i => i.IsError || i.Code == "unknown" && !i.Detail.Contains("javafml")), loader + minecraft);
            }
        });

        await check("TOML Minecraft and loader minima block incompatible updates", async () =>
        {
            foreach (var loader in new[] { LoaderType.Forge, LoaderType.NeoForge })
                foreach (bool minecraft in new[] { true, false })
                {
                    var f = new Fixture(root, "range-" + loader + minecraft, loader, "1.21.1", loader == LoaderType.Forge ? "52.0.1" : "21.1.10");
                    string id = minecraft ? "minecraft" : loader.ToString().ToLowerInvariant();
                    string range = minecraft ? "[1.21.2,)" : loader == LoaderType.Forge ? "[52.0.9,)" : "[21.1.200,)";
                    f.Mod("main.jar", "main", "1", Dep("main", id, range, loader));
                    Require((await f.Check()).Issues.Any(i => i.Code == "version_range" && i.IsError && i.Detail.Contains(id)), id);
                }
        });

        await check("TOML required and optional client dependencies follow side and presence", async () =>
        {
            foreach (var loader in new[] { LoaderType.Forge, LoaderType.NeoForge })
            {
                var f = new Fixture(root, "dependencies-" + loader, loader);
                f.Mod("main.jar", "main", "1", Dep("main", "client_required", "[2,)", loader, side: "CLIENT")
                    + Dep("main", "server_required", "[2,)", loader, side: "SERVER")
                    + Dep("main", "optional_absent", "[2,)", loader, kind: "optional")
                    + Dep("main", "optional_present", "[2,)", loader, kind: "optional"));
                f.Mod("optional.jar", "optional_present", "1");
                var issues = (await f.Check()).Issues;
                Require(issues.Any(i => i.Code == "dependency" && i.IsError && i.Detail.Contains("client_required")));
                Require(issues.Any(i => i.Code == "version_range" && i.IsError && i.Detail.Contains("optional_present")));
                Require(!issues.Any(i => i.Detail.Contains("server_required") || i.Detail.Contains("optional_absent")));
                f.Mod("client.jar", "client_required", "2");
                Require(!(await f.Check()).Issues.Any(i => i.Code == "dependency"));
            }
        });

        await check("NeoForge defaults dependencies to required and separates incompatible from discouraged", async () =>
        {
            var f = new Fixture(root, "neo-kinds", LoaderType.NeoForge);
            f.Mod("main.jar", "main", "1", "\n[[dependencies.main]]\nmodId='absent'\n"
                + Dep("main", "bad", "[1,2)", LoaderType.NeoForge, kind: "incompatible")
                + Dep("main", "unwise", "[1,2)", LoaderType.NeoForge, kind: "discouraged"));
            f.Mod("bad.jar", "bad", "1"); f.Mod("unwise.jar", "unwise", "1");
            var issues = (await f.Check()).Issues;
            Require(issues.Any(i => i.Code == "dependency" && i.IsError && i.Detail.Contains("absent")));
            Require(issues.Any(i => i.Code == "conflict" && i.IsError && i.Detail.Contains("bad")));
            Require(issues.Any(i => i.Code == "conflict" && !i.IsError && i.Detail.Contains("unwise")));
        });

        await check("TOML manifest version placeholders are resolved before dependency comparisons", async () =>
        {
            foreach (bool manifest in new[] { true, false })
            {
                var f = new Fixture(root, "manifest-" + manifest, LoaderType.Forge);
                f.Mod("main.jar", "main", "1", Dep("main", "library", "[2.4.0]", LoaderType.Forge));
                f.Write("library.jar", Jar((f.Metadata, Utf8(Toml("library", "${file.jarVersion}"))),
                    ("META-INF/MANIFEST.MF", Utf8(manifest ? "Manifest-Version: 1.0\r\nImplementation-Version: 2.\r\n 4.0\r\n\r\n" : "Manifest-Version: 1.0\r\n"))));
                var issues = (await f.Check()).Issues;
                Require(!issues.Any(i => i.IsError));
                Require(issues.Any(i => i.Code == "unknown" && i.Detail.Contains("library")) == !manifest);
            }
        });

        await check("Forge legacy mandatory and modern NeoForge dependency type use their own metadata schema", async () =>
        {
            foreach (bool modern in new[] { true, false })
            {
                var f = new Fixture(root, "dependency-schema-" + modern, LoaderType.NeoForge, modern ? "1.21.1" : "1.20.1",
                    modern ? "21.1.219" : "47.1.106");
                f.Mod("main.jar", "main", "1", "\n[[dependencies.main]]\nmodId='absent'\nmandatory="
                    + (modern ? "false" : "true") + "\n" + (modern ? "" : "type='optional'\n"));
                Require((await f.Check()).Issues.Any(i => i.Code == "dependency" && i.IsError && i.Detail.Contains("absent")));
            }
            var neo = new Fixture(root, "dependency-type-case", LoaderType.NeoForge);
            neo.Mod("main.jar", "main", "1", "\n[[dependencies.main]]\nmodId='absent'\ntype='OPTIONAL'\n");
            Require(!(await neo.Check()).Issues.Any(i => i.Code == "dependency" || i.IsError));
        });

        await check("TOML multiline descriptions comments quoted keys and arrays cannot create phantom dependencies", async () =>
        {
            var f = new Fixture(root, "toml-text", LoaderType.Forge);
            string metadata = "modLoader='javafml'\nloaderVersion='[47,)'\n[[mods]]\n\"modId\"='main'\nversion=\"1\\u002e0\"\n"
                + "description='''\n[[dependencies.main]]\nmodId='phantom'\n# not a comment\nends in a quote''''\n"
                + "authors=[\n 'one', # actual comment\n 'two'\n]\n"
                + "[[ dependencies . \"main\" ]] # real table\nmodId='minecraft'\nmandatory=true\nversionRange='[1.20.1]'\nside='CLIENT'\n";
            f.Write("main.jar", Jar((f.Metadata, Utf8(metadata))));
            Require(!(await f.Check()).Issues.Any(), "TOML text parsing");
        });

        await check("JarJar nested mods satisfy required TOML dependencies", async () =>
        {
            var f = new Fixture(root, "jarjar", LoaderType.NeoForge);
            string nestedPath = "META-INF/jarjar/library.jar";
            byte[] nested = Jar((f.Metadata, Utf8(Toml("library", "2.0"))));
            f.Write("main.jar", Jar((f.Metadata, Utf8(Toml("main", "1", Dep("main", "library", "[2,3)", LoaderType.NeoForge)))),
                (nestedPath, nested), ("META-INF/jarjar/metadata.json", JsonSerializer.SerializeToUtf8Bytes(new
                { jars = new[] { new { identifier = new { group = "test", artifact = "library" }, version = new { range = "[2,3)", artifactVersion = "2.0" }, path = nestedPath } } }))));
            Require(!(await f.Check()).Issues.Any(i => i.IsError || i.Code == "dependency"));
        });

        await check("Forge language loader major is distinct from modern NeoForge FML", async () =>
        {
            foreach (var (loader, version, range, error) in new[]
            {
                (LoaderType.Forge, "1.20.1-47.4.0", "[47,48)", false), (LoaderType.Forge, "47.4.0", "[48,)", true),
                (LoaderType.NeoForge, "21.1.219", "[4,5)", false)
            })
            {
                var f = new Fixture(root, "language-" + loader + error, loader, loader == LoaderType.Forge ? "1.20.1" : "1.21.1", version);
                f.Write("main.jar", Jar((f.Metadata, Utf8(Toml("main", "1").Replace("loaderVersion='[1,)'", "loaderVersion='" + range + "'", StringComparison.Ordinal)))));
                var issues = (await f.Check()).Issues;
                Require(issues.Any(i => i.IsError) == error);
                if (loader == LoaderType.NeoForge) Require(issues.Any(i => i.Code == "unknown" && i.Detail.Contains("javafml")));
            }
        });

        await check("TOML unknown version syntax stays unverified and duplicate mod IDs are errors", async () =>
        {
            var f = new Fixture(root, "unknown-duplicate", LoaderType.Forge);
            f.Mod("main.jar", "main", "1", Dep("main", "library", "[2,)", LoaderType.Forge));
            f.Mod("library.jar", "library", "custom-build");
            var issues = (await f.Check()).Issues;
            Require(!issues.Any(i => i.IsError) && issues.Any(i => i.Code == "unknown" && i.Detail.Contains("library")));
            f.Mod("duplicate.jar", "library", "custom-build");
            Require((await f.Check()).Issues.Any(i => i.Code == "duplicate" && i.IsError));
        });

        await check("Forge 1.12.2 mcmod info controls preserve IDs and malformed metadata stays unreadable", async () =>
        {
            var f = new Fixture(root, "legacy", LoaderType.Forge, "1.12.2", "14.23.5.2859");
            f.Write("first.jar", Jar(("mcmod.info", Utf8("[{\"modid\":\"legacy\",\"version\":\"1.0\",\"description\":\"First \\\"quoted\\\" line\nSecond\tline\"}]"))));
            Require(!(await f.Check()).Issues.Any(i => i.IsError));
            f.Write("second.jar", Jar(("mcmod.info", Utf8("{\"modList\":[{\"modid\":\"legacy\",\"version\":\"2.0\"}]}"))));
            Require((await f.Check()).Issues.Any(i => i.IsError && i.Code == "duplicate"));
            f.Write("broken.jar", Jar(("mcmod.info", Utf8("[{\"modid\":\"broken\",\"description\":\"unclosed\n}]"))));
            Require((await f.Check()).Issues.Any(i => i.IsError && i.Code == "unreadable" && i.Detail == "broken.jar"));
        });

        await check("Fabric server environment dependencies do not block a client and missing required remains a warning", async () =>
        {
            var f = new Fixture(root, "fabric-environment", LoaderType.Fabric, "1.21.1", "0.19.3");
            f.Write("server.jar", Jar(("fabric.mod.json", Utf8("{\"schemaVersion\":1,\"id\":\"server\",\"version\":\"1\",\"environment\":\"server\",\"depends\":{\"server_library\":\"*\"}}"))));
            Require(!(await f.Check()).Issues.Any());
            f.Write("client.jar", Jar(("fabric.mod.json", Utf8("{\"schemaVersion\":1,\"id\":\"client\",\"version\":\"1\",\"environment\":\"client\",\"depends\":{\"client_library\":\"*\"}}"))));
            var issue = (await f.Check()).Issues.Single();
            Require(issue.Code == "dependency" && !issue.IsError && issue.Detail.Contains("client_library"));
        });

        await check("Fabric overrides remove then add while explicit replacement suppresses both operations", async () =>
        {
            foreach (bool replace in new[] { true, false })
            {
                var f = new Fixture(root, "fabric-overrides-" + replace, LoaderType.Fabric, "1.21.1", "0.19.3");
                f.Write("main.jar", Jar(("fabric.mod.json", Utf8("{\"schemaVersion\":1,\"id\":\"main\",\"version\":\"1\",\"depends\":{\"minecraft\":\">=1.22\",\"absent\":\"*\"},\"breaks\":{\"minecraft\":\"*\"}}"))));
                Directory.CreateDirectory(Path.Combine(f.Game, "config"));
                string operations = replace ? "\"depends\":{\"minecraft\":\"1.21.1\"},\"+depends\":{\"wrong\":\"*\"}" :
                    "\"-depends\":{\"minecraft\":\"*\",\"absent\":\"*\"},\"+depends\":{\"minecraft\":\"1.21.1\"}";
                await File.WriteAllTextAsync(Path.Combine(f.Game, "config", "fabric_loader_dependencies.json"),
                    "{\"version\":1,\"overrides\":{\"main\":{\"-breaks\":{\"minecraft\":\"*\"}," + operations + "}}}");
                Require(!(await f.Check()).Issues.Any());
            }
        });

        await check("Fabric additive overrides keep the existing requirement and invalid override formats are errors", async () =>
        {
            var f = new Fixture(root, "fabric-additive", LoaderType.Fabric, "1.21.1", "0.19.3");
            f.Write("main.jar", Jar(("fabric.mod.json", Utf8("{\"schemaVersion\":1,\"id\":\"main\",\"version\":\"1\",\"depends\":{\"minecraft\":\">=1.22\"}}"))));
            Directory.CreateDirectory(Path.Combine(f.Game, "config"));
            string path = Path.Combine(f.Game, "config", "fabric_loader_dependencies.json");
            await File.WriteAllTextAsync(path, "{\"version\":1,\"overrides\":{\"main\":{\"+depends\":{\"minecraft\":\"1.21.1\"}}}}");
            Require((await f.Check()).Issues.Any(i => i.Code == "version_range" && i.IsError));
            await File.WriteAllTextAsync(path, "{\"version\":2,\"overrides\":{}}");
            Require((await f.Check()).Issues.Any(i => i.Code == "unreadable" && i.IsError && i.Detail.EndsWith(".json")));
        });

        await check("Fabric overrides address real mod IDs and do not mutate provides aliases", async () =>
        {
            var f = new Fixture(root, "fabric-alias-override", LoaderType.Fabric, "1.21.1", "0.19.3");
            f.Write("main.jar", Jar(("fabric.mod.json", Utf8("{\"schemaVersion\":1,\"id\":\"main\",\"version\":\"1\",\"provides\":[\"alias\"]}"))));
            Directory.CreateDirectory(Path.Combine(f.Game, "config"));
            await File.WriteAllTextAsync(Path.Combine(f.Game, "config", "fabric_loader_dependencies.json"),
                "{\"version\":1,\"overrides\":{\"alias\":{\"+depends\":{\"missing\":\"*\"}}}}");
            Require(!(await f.Check()).Issues.Any());
        });
    }

    private static string Toml(string id, string version, string dependencies = "") =>
        "modLoader='javafml'\nloaderVersion='[1,)'\nlicense='MIT'\n[[mods]]\nmodId=" + JsonSerializer.Serialize(id)
        + "\nversion=" + JsonSerializer.Serialize(version) + "\n" + dependencies;
    private static string Dep(string owner, string id, string range, LoaderType loader, string kind = "required", string side = "BOTH") =>
        "\n[[dependencies." + owner + "]]\nmodId=" + JsonSerializer.Serialize(id) + "\n"
        + (loader == LoaderType.NeoForge ? "type=" + JsonSerializer.Serialize(kind) : "mandatory=" + (kind == "required" ? "true" : "false"))
        + "\nversionRange=" + JsonSerializer.Serialize(range) + "\nside=" + JsonSerializer.Serialize(side) + "\n";
    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);
    private static byte[] Jar(params (string Path, byte[] Bytes)[] entries)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
            foreach (var entry in entries) { using var output = zip.CreateEntry(entry.Path).Open(); output.Write(entry.Bytes); }
        return memory.ToArray();
    }
    private static void Require(bool value, string? detail = null) { if (!value) throw new Exception("Loader compatibility assertion: " + detail); }
    private sealed class Fixture
    {
        public string Game { get; }
        private GameInstance Instance { get; }
        public string Metadata => Instance.Loader == LoaderType.NeoForge && Instance.McVersion != "1.20.1" ? "META-INF/neoforge.mods.toml" : "META-INF/mods.toml";
        public Fixture(string root, string name, LoaderType loader, string? minecraft = null, string? version = null)
        {
            Game = Directory.CreateDirectory(Path.Combine(root, "loader-compatibility", name)).FullName;
            Instance = new() { Id = name, McVersion = minecraft ?? (loader == LoaderType.NeoForge ? "1.21.1" : "1.20.1"),
                Loader = loader, LoaderVersion = version ?? (loader == LoaderType.NeoForge ? "21.1.219" : "47.4.0") };
        }
        public void Write(string file, byte[] bytes) { Directory.CreateDirectory(Path.Combine(Game, "mods")); File.WriteAllBytes(Path.Combine(Game, "mods", file), bytes); }
        public void Mod(string file, string id, string version, string dependencies = "") => Write(file, Jar((Metadata, Utf8(Toml(id, version, dependencies)))));
        public Task<CompatibilityReport> Check() => new ModCompatibilityChecker().CheckAsync(Instance, Game, false);
    }
}
