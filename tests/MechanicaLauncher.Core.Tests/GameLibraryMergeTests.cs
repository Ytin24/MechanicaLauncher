using System.Text.Json;
using MechanicaLauncher.Core.Game;
using MechanicaLauncher.Core.Models;

internal static class GameLibraryMergeTests
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> check, string root)
    {
        await check("Minecraft 1.16 libraries retain the active platform and its native classifiers", async () =>
        {
            var windows = new List<Rule> { new() { Action = "allow" }, new() { Action = "disallow", Os = new() { Name = "osx" } } };
            var mac = new List<Rule> { new() { Action = "allow", Os = new() { Name = "osx" } } };
            var native = new LibraryArtifact { Path = "glfw-native.jar", Url = "https://example.invalid/native" };
            var parent = new List<Library>
            {
                new() { Name = "org.lwjgl:lwjgl-glfw:3.2.1", Rules = mac, Downloads = new() { Artifact = new() { Path = "mac.jar" } } },
                new() { Name = "org.lwjgl:lwjgl-glfw:3.2.2", Rules = windows, Downloads = new() { Artifact = new() { Path = "glfw.jar" } } },
                new() { Name = "org.lwjgl:lwjgl-glfw:3.2.2", Rules = windows, Natives = new() { ["windows"] = "natives-windows" },
                    Downloads = new() { Artifact = new() { Path = "glfw.jar" }, Classifiers = new() { ["natives-windows"] = native } } }
            };
            var merged = await Merge(parent, [], "platform");
            Require(merged.Libraries.Count == 2 && merged.Libraries.All(l => l.Name.EndsWith("3.2.2")), "The macOS entry hid the Windows library.");
            Require(merged.Libraries.Select(AssetDownloader.GetNativeArtifact).Count(a => a?.Path == native.Path) == 1, "The native classifier was dropped during inheritance.");
            string libs = Path.Combine(root, "merge-libraries");
            Directory.CreateDirectory(libs);
            File.WriteAllText(Path.Combine(libs, "glfw.jar"), "classpath fixture");
            string classpath = GameLauncher.BuildClasspath(merged, "client.jar", libs, true);
            Require(classpath.Split(Path.PathSeparator).Count(p => p.EndsWith("glfw.jar")) == 1 && !classpath.Contains("mac.jar"), "Classpath contains missing or duplicate platform entries.");
        });
        await check("Disabled loader libraries cannot hide a vanilla dependency", async () =>
        {
            var merged = await Merge([new() { Name = "org.ow2.asm:asm:9.7" }],
                [new() { Name = "org.ow2.asm:asm:9.8", Rules = [new() { Action = "allow", Os = new() { Name = "osx" } }] }], "disabled-child");
            Require(merged.Libraries.Count == 1 && merged.Libraries[0].Name.EndsWith("9.7"), "An inapplicable child removed the parent library.");
        });
        await check("Applicable loader overrides still remove older library versions", async () =>
        {
            var merged = await Merge([new() { Name = "org.ow2.asm:asm:9.7" }, new() { Name = "org.lwjgl:lwjgl:3.3.3:natives-windows" }],
                [new() { Name = "org.ow2.asm:asm:9.8" }, new() { Name = "org.lwjgl:lwjgl:3.3.3" }], "override");
            Require(merged.Libraries.Count == 3 && merged.Libraries.All(l => l.Name != "org.ow2.asm:asm:9.7"), "The child override or classifier separation was lost.");
        });

        async Task<VersionMeta> Merge(List<Library> parent, List<Library> child, string name)
        {
            string shared = Path.Combine(root, "library-merge-" + name), game = Path.Combine(shared, "game");
            Directory.CreateDirectory(Path.Combine(shared, "versions", "parent"));
            Directory.CreateDirectory(Path.Combine(game, "versions", "child"));
            await File.WriteAllTextAsync(Path.Combine(shared, "versions", "parent", "parent.json"),
                JsonSerializer.Serialize(new VersionMeta { Id = "parent", MainClass = "net.minecraft.Main", Libraries = parent }));
            await File.WriteAllTextAsync(Path.Combine(game, "versions", "child", "child.json"),
                JsonSerializer.Serialize(new VersionMeta { Id = "child", InheritsFrom = "parent", MainClass = "loader.Main", Libraries = child }));
            return await new VersionManager(shared).GetMergedMetaAsync("child", game);
        }
    }

    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
