using System.Net;
using System.Text;
using System.Text.Json;
using MechanicaLauncher.Core.Game;

internal static class NeoForgeVersionTests
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check, string root)
    {
        await check("NeoForge 26.3 selection pins the omitted Minecraft patch to zero", async () =>
        {
            using var http = VersionsHttp("neoforge", ["26.3.0.2-beta", "26.3.1.4-beta", "26.30.0.1-beta",
                "26.3.0.23-beta", "26.3.0.11-beta", "26.3.0.23-beta", "26.3.0.999.extra", "26.3.0.not-a-build"]);
            var versions = await new NeoForgeInstaller(root, root, http).GetVersionsAsync("26.3");
            Require(versions.SequenceEqual(["26.3.0.23-beta", "26.3.0.11-beta", "26.3.0.2-beta"]));
        });

        await check("NeoForge calendar Minecraft patch versions do not mix with the base release", async () =>
        {
            using var http = VersionsHttp("neoforge", ["26.3.0.23-beta", "26.3.1.1-beta", "26.3.1.10", "26.3.10.1", "26.4.0.1-beta"]);
            var versions = await new NeoForgeInstaller(root, root, http).GetVersionsAsync("26.3.1");
            Require(versions.SequenceEqual(["26.3.1.10", "26.3.1.1-beta"]));
        });

        await check("NeoForge legacy numbering still separates 1.21 and 1.21.1", async () =>
        {
            foreach (var (minecraft, expected) in new[] { ("1.21", "21.0.167"), ("1.21.1", "21.1.219"), ("1.20.4", "20.4.251") })
            {
                using var http = VersionsHttp("neoforge", ["21.0.167", "21.1.219", "21.10.5", "20.4.251", "20.40.5"]);
                var versions = await new NeoForgeInstaller(root, root, http).GetVersionsAsync(minecraft);
                Require(versions.SequenceEqual([expected]));
            }
        });

        await check("NeoForge 1.20.1 uses the official forge artifact version list", async () =>
        {
            using var http = VersionsHttp("forge", ["1.20.1-47.1.9", "1.20.1-47.1.100", "1.20.1-47.1.106", "1.20.1-47.1.105",
                "47.1.82", "1.20.2-48.0.1", "20.1.1"]);
            var versions = await new NeoForgeInstaller(root, root, http).GetVersionsAsync("1.20.1");
            Require(versions.SequenceEqual(["47.1.106", "47.1.105", "47.1.100", "47.1.9"]));
        });

        await check("NeoForge required client paths match the official installer PATCHED entries", () =>
        {
            foreach (var fixture in new[]
            {
                (Minecraft: "1.20.1", Version: "47.1.106", Artifact: "forge/1.20.1-47.1.106/forge-1.20.1-47.1.106",
                    Profile: """{"inheritsFrom":"1.20.1","mainClass":"cpw.mods.bootstraplauncher.BootstrapLauncher"}""",
                    Install: """{"data":{"PATCHED":{"client":"[net.neoforged:forge:1.20.1-47.1.106:client]"}}}"""),
                (Minecraft: "26.3", Version: "26.3.0.23-beta", Artifact: "neoforge/26.3.0.23-beta/neoforge-26.3.0.23-beta",
                    Profile: """{"inheritsFrom":"26.3","mainClass":"net.neoforged.fml.startup.Client"}""",
                    Install: """{"data":{"PATCHED":{"client":"[net.neoforged:minecraft-client-patched:26.3.0.23-beta]"}}}""")
            })
            {
                using var profile = JsonDocument.Parse(fixture.Profile);
                using var install = JsonDocument.Parse(fixture.Install);
                Require(profile.RootElement.GetProperty("inheritsFrom").GetString() == fixture.Minecraft);
                var artifact = NeoForgeInstaller.GetInstallArtifact(fixture.Minecraft, fixture.Version);
                Require(artifact.InstallerUrl == "https://maven.neoforged.net/releases/net/neoforged/" + fixture.Artifact + "-installer.jar");
                string coordinate = install.RootElement.GetProperty("data").GetProperty("PATCHED").GetProperty("client").GetString()![1..^1];
                string[] parts = coordinate.Split(':');
                string path = parts[0].Replace('.', '/') + "/" + parts[1] + "/" + parts[2] + "/" + parts[1] + "-" + parts[2] +
                    (parts.Length == 4 ? "-" + parts[3] : "") + ".jar";
                Require(artifact.RequiredLibrary == path);
            }
            Require(NeoForgeInstaller.GetInstallArtifact("1.20.1", "1.20.1-47.1.106") == NeoForgeInstaller.GetInstallArtifact("1.20.1", "47.1.106"));
            Require(NeoForgeInstaller.GetInstallArtifact("1.21.1", "21.1.219").RequiredLibrary == "net/neoforged/neoforge/21.1.219/neoforge-21.1.219-client.jar");
            return Task.CompletedTask;
        });

        await check("NeoForge rejects cross-target and unsafe versions before any installation request", async () =>
        {
            using var http = new HttpClient(new Handler(_ => throw new Exception("Unexpected network request")));
            var installer = new NeoForgeInstaller(root, root, http);
            foreach (var (minecraft, version) in new[] { ("26.3", "26.3.1.1"), ("26.3.1", "26.3.0.23-beta"),
                ("1.21.1", "21.0.167"), ("1.20.1", "20.1.1"), ("1.20.1", "47.2.1"), ("1.21.1", "21.1.219/../../outside"),
                ("1.12.2", "14.23.5.2860"), ("1.16.5", "36.2.42") })
            {
                bool rejected = false;
                try { await installer.InstallAsync(minecraft, version); }
                catch (InvalidDataException) { rejected = true; }
                Require(rejected);
            }
            Require((await installer.GetVersionsAsync("1.12.2")).Count == 0);
            Require((await installer.GetVersionsAsync("1.16.5")).Count == 0);
        });

        await check("NeoForge metadata cancellation and HTTP failures propagate", async () =>
        {
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.ServiceUnavailable)));
            var installer = new NeoForgeInstaller(root, root, http);
            bool cancelled = false, unavailable = false;
            try { await installer.GetVersionsAsync("26.3", cancel.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            try { await installer.GetVersionsAsync("26.3"); }
            catch (HttpRequestException) { unavailable = true; }
            Require(cancelled && unavailable);
        });
    }

    private static HttpClient VersionsHttp(string artifact, string[] versions) => new(new Handler(request =>
    {
        Require(request.RequestUri!.AbsoluteUri == "https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/" + artifact);
        return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { isSnapshot = false, versions }), Encoding.UTF8, "application/json") };
    }));
    private static void Require(bool condition) { if (!condition) throw new Exception("NeoForge version assertion failed."); }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }
}
