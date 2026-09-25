using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Web;
using MechanicaLauncher.Core.Instances;
using MechanicaLauncher.Core.Mods;
using MechanicaLauncher.Core.Models;
using MechanicaLauncher.Views;

namespace MechanicaLauncher;

internal static partial class UiSmoke
{
    private sealed partial class SmokeApp
    {
        private async Task CheckCatalogFilters(Frame frame, GameInstance instance)
        {
            await using var server = new CatalogServer();
            var http = (HttpClient)typeof(ModrinthClient).GetField("DefaultHttp", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            http.BaseAddress = server.Address;
            var manager = new InstanceManager();
            var other = manager.CreateInstance("Другая версия", "1.20.1", LoaderType.Forge, "47.0.0");
            Settings.SelectedInstanceId = instance.Id;
            Settings.Save();
            ModsPage catalog = null!;

            async Task SearchReady(string type, string? version, int? offset = null)
            {
                await Until(() =>
                {
                    var request = server.Requests.LastOrDefault(r => r.AbsolutePath == "/v2/search");
                    if (request == null || catalog.FindName("ResultsInfo") is not TextBlock info || info.Text == L("mods.searching")) return false;
                    var query = HttpUtility.ParseQueryString(request.Query);
                    var facets = JsonSerializer.Deserialize<string[][]>(query["facets"]!)!.SelectMany(f => f).ToArray();
                    return facets.Contains("project_type:" + type) &&
                        (version == null ? !facets.Any(f => f.StartsWith("versions:")) : facets.Contains("versions:" + version)) &&
                        (offset == null || query["offset"] == offset.ToString());
                });
            }
            async Task SelectType(string type)
            {
                var tabs = (Grid)catalog.FindName("TypeTabs");
                await Invoke(tabs.Children.OfType<Button>().Single(b => b.Content as string == L("mods.type." + type)));
            }
            async Task OpenPack(int expectedVersions)
            {
                await Invoke(((StackPanel)catalog.FindName("SearchResultsPanel")).Children.OfType<Button>().First(b => b.Content is Grid));
                await Until(() => frame.Content is ModProjectPage page &&
                    ((ComboBox)page.FindName("VersionPicker")).Items.Count == expectedVersions &&
                    ((ComboBox)page.FindName("VersionPicker")).IsEnabled);
            }

            foreach (var (theme, width) in new[] { (ElementTheme.Light, 800), (ElementTheme.Dark, 1100) })
            {
                ((FrameworkElement)MainWindow.Content).RequestedTheme = theme;
                MainWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32(width, 720));
                Require(frame.Navigate(typeof(ModsPage)), "Catalog navigation failed");
                catalog = (ModsPage)frame.Content;
                foreach (var type in new[] { "mod", "modpack", "shader", "resourcepack", "datapack" })
                {
                    await SelectType(type);
                    await SearchReady(type, instance.McVersion);
                    var filter = (CheckBox)catalog.FindName("CompatibleOnly");
                    Require(filter.Visibility == Visibility.Visible && filter.IsEnabled && filter.IsChecked == true, "Version filter hidden or disabled for " + type);
                    Require(filter.Content.ToString()!.Contains(instance.McVersion), "Filter does not show Minecraft version");
                    var query = HttpUtility.ParseQueryString(server.Requests.Last(r => r.AbsolutePath == "/v2/search").Query);
                    var facets = JsonSerializer.Deserialize<string[][]>(query["facets"]!)!.SelectMany(f => f).ToArray();
                    Require(facets.Contains("categories:fabric") == (type == "mod"), "Instance loader leaked into " + type);
                    Require(facets.Contains("categories:datapack") == (type == "datapack"), "Datapack loader filter missing");
                    if (type == "modpack") await Capture((FrameworkElement)MainWindow.Content, $"catalog-filter-{theme}-{width}.png");
                    File.AppendAllText(_log, $"PASS Catalog {type} {theme} {width} Minecraft {instance.McVersion}\n");
                }
            }

            await SelectType("modpack");
            await SearchReady("modpack", instance.McVersion);
            await Invoke(((StackPanel)catalog.FindName("SearchResultsPanel")).Children.OfType<Button>().Single(b => b.Content is string));
            await SearchReady("modpack", instance.McVersion, 1);
            File.AppendAllText(_log, "PASS Catalog pagination keeps the Minecraft filter\n");
            await OpenPack(1);
            var details = (ModProjectPage)frame.Content;
            var version = (ModrinthVersion)((ComboBoxItem)((ComboBox)details.FindName("VersionPicker")).SelectedItem).Tag;
            Require(version.GameVersions.SequenceEqual(new[] { instance.McVersion }) && version.Loaders.Contains("forge"), "Modpack details selected the wrong Minecraft version or imposed Fabric");
            var versionQuery = HttpUtility.ParseQueryString(server.Requests.Last(r => r.AbsolutePath.EndsWith("/version")).Query);
            Require(JsonSerializer.Deserialize<string[]>(versionQuery["game_versions"]!)!.Single() == instance.McVersion && versionQuery["loaders"] == null, "Modpack version request dropped the catalog filter");
            Require(((TextBlock)details.FindName("TargetSummary")).Text.Contains(instance.McVersion), "Modpack Minecraft filter is not visible in details");
            File.AppendAllText(_log, "PASS Modpack releases preserve the catalog version and reject unrelated API results\n");

            frame.GoBack();
            await Until(() => frame.Content is ModsPage);
            catalog = (ModsPage)frame.Content;
            var picker = (ComboBox)catalog.FindName("InstancePicker");
            picker.SelectedItem = picker.Items.OfType<ComboBoxItem>().Single(i => ((GameInstance)i.Tag).Id == other.Id);
            await SearchReady("modpack", other.McVersion, 0);
            Require(((CheckBox)catalog.FindName("CompatibleOnly")).Content.ToString()!.Contains(other.McVersion), "Changing instance left a stale filter caption");
            File.AppendAllText(_log, "PASS Changing instance updates the version filter and resets pagination\n");

            ((CheckBox)catalog.FindName("CompatibleOnly")).IsChecked = false;
            await SearchReady("modpack", null);
            Require(((TextBlock)catalog.FindName("ResultsInfo")).Text.Contains(L("catalog.all_versions")), "Unfiltered catalog is not labelled");
            await OpenPack(3);
            details = (ModProjectPage)frame.Content;
            Require(((TextBlock)details.FindName("TargetSummary")).Text == L("catalog.all_versions"), "Unfiltered modpack details still show a version restriction");
            Require(HttpUtility.ParseQueryString(server.Requests.Last(r => r.AbsolutePath.EndsWith("/version")).Query)["game_versions"] == null, "All versions still sends a Minecraft constraint");
            File.AppendAllText(_log, "PASS All versions applies to both the catalog and modpack releases\n");

            frame.GoBack();
            await Until(() => frame.Content is ModsPage);
            catalog = (ModsPage)frame.Content;
            ((ComboBox)catalog.FindName("InstancePicker")).SelectedIndex = -1;
            ((CheckBox)catalog.FindName("CompatibleOnly")).IsChecked = true;
            await SearchReady("modpack", null);
            Require(!((CheckBox)catalog.FindName("CompatibleOnly")).IsEnabled, "Version filter enabled without an instance");
            File.AppendAllText(_log, "PASS Catalog without an instance stays unfiltered\n");

            frame.Navigate(typeof(ModProjectPage), new ModProjectRequest(new ModrinthProject { ProjectId = "catalog-modpack", Title = "Нет совместимого файла" }, "modpack", instance.Id, "missing-version"));
            await Until(() => frame.Content is ModProjectPage page &&
                ((TextBlock)page.FindName("CompatibilityInfo")).Text == L("catalog.pack_no_versions"));
            details = (ModProjectPage)frame.Content;
            Require(!((Button)details.FindName("InstallButton")).IsEnabled && ((ComboBox)details.FindName("VersionPicker")).Items.Count == 0, "A different Minecraft release was offered when no match exists");
            File.AppendAllText(_log, "PASS Missing modpack release blocks installation with a clear message\n");
            Settings.SelectedInstanceId = instance.Id;
            Settings.Save();
            frame.Navigate(typeof(InstancesPage));
        }

        private static async Task Until(Func<bool> ready)
        {
            var deadline = DateTime.UtcNow.AddSeconds(6);
            while (!ready())
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("Catalog UI did not finish the requested transition.");
                await Task.Delay(30);
            }
        }

        private sealed class CatalogServer : IAsyncDisposable
        {
            private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
            private readonly CancellationTokenSource _stop = new();
            private readonly Task _worker;
            public ConcurrentQueue<Uri> Requests { get; } = new();
            public Uri Address { get; }

            public CatalogServer()
            {
                _listener.Start();
                Address = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}");
                _worker = ServeAsync();
            }
            private async Task ServeAsync()
            {
                try
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        using var connection = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                        await using var stream = connection.GetStream();
                        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                        var first = await reader.ReadLineAsync(_stop.Token).ConfigureAwait(false);
                        if (first == null) continue;
                        while (await reader.ReadLineAsync(_stop.Token).ConfigureAwait(false) is { Length: > 0 }) { }
                        var request = new Uri(Address, first.Split(' ')[1]);
                        Requests.Enqueue(request);
                        var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Response(request)));
                        var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
                        await stream.WriteAsync(headers, _stop.Token).ConfigureAwait(false);
                        await stream.WriteAsync(payload, _stop.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
                catch (SocketException) when (_stop.IsCancellationRequested) { }
            }
            private static object Response(Uri request)
            {
                if (request.AbsolutePath == "/v2/search")
                {
                    var query = HttpUtility.ParseQueryString(request.Query);
                    var facets = JsonSerializer.Deserialize<string[][]>(query["facets"]!)!.SelectMany(f => f).ToArray();
                    var type = facets.Single(f => f.StartsWith("project_type:"))["project_type:".Length..];
                    return new { total_hits = 2, hits = new[] { new { project_id = "catalog-" + type, project_type = type,
                        title = "Тестовый проект", description = "Описание проекта для проверки фильтра Minecraft.", author = "Test", downloads = 42 } } };
                }
                if (request.AbsolutePath.EndsWith("/version"))
                    return new[] { ("26.3", "2026-09-01"), ("1.21.1", "2025-01-01"), ("1.20.1", "2024-01-01") }
                        .Select(v => new { id = v.Item1, version_number = v.Item1, version_type = "release", date_published = v.Item2 + "T00:00:00Z",
                            game_versions = new[] { v.Item1 }, loaders = new[] { "forge" },
                            files = new[] { new { filename = "test.mrpack", primary = true, size = 1 } } }).ToArray();
                return new { id = "catalog-modpack", title = "Тестовый модпак", description = "Описание.", body = "# Тестовый модпак", gallery = Array.Empty<object>() };
            }
            public async ValueTask DisposeAsync()
            {
                _stop.Cancel();
                _listener.Stop();
                await _worker;
                _stop.Dispose();
            }
        }
    }
}
