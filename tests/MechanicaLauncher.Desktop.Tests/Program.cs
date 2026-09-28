using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MechanicaLauncher.Desktop;
using MechanicaLauncher.Core.Game;
using MechanicaLauncher.Core.Instances;
using MechanicaLauncher.Core.IO;
using MechanicaLauncher.Core.Mods;
using MechanicaLauncher.Core.Models;
using MechanicaLauncher.Core.Profiles;
using MechanicaLauncher.Core.Config;
using MechanicaLauncher.Core.Servers;
using Nitidus;
using Nitidus.Native;

internal static partial class Program
{
    private static int checks;
    private static string output = "";
    [STAThread]
    private static int Main(string[] args)
    {
        output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../out/nitidus-ui-tests-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("MECHANICA_DATA_DIR", Path.Combine(output, "data"));
        using var context = new Pump();
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            if (args.Contains("--server-sync-smoke")) return ServerSyncSmoke(args, context);
            if (args.Contains("--server-discovery"))
            {
                ServerDiscoveryChecks(context);
                Console.WriteLine($"PASS {checks} server discovery checks");
                Console.WriteLine(output);
                return 0;
            }
            if (args.Contains("--server-sync-settings"))
            {
                ServerSyncSettingsChecks(context);
                Console.WriteLine($"PASS {checks} server sync settings checks");
                Console.WriteLine(output);
                return 0;
            }
            if (args.Contains("--bundled-bridge"))
            {
                BundledBridgeChecks(context);
                Console.WriteLine($"PASS {checks} bundled bridge checks");
                Console.WriteLine(output);
                return 0;
            }
            if (args.Contains("--bridge-lifecycle"))
            {
                GameBridgeLifecycleChecks(context);
                Console.WriteLine($"PASS {checks} bridge lifecycle checks");
                Console.WriteLine(output);
                return 0;
            }
            if (args.Contains("--server-sync") || args.Contains("--mod-updates"))
            {
                if (args.Contains("--server-sync")) ServerSyncChecks(context);
                if (args.Contains("--mod-updates")) ModUpdateChecks(context);
                Console.WriteLine($"PASS {checks} server sync / mod update checks");
                Console.WriteLine(output);
                return 0;
            }
            if (args.Contains("--window")) return WindowChecks(args.Contains("--end-session"));
            if (args.Contains("--installed-content"))
            {
                InstalledContentChecks(context);
                Console.WriteLine($"PASS {checks} installed content checks");
                Console.WriteLine(output);
                return 0;
            }
            if (args.Contains("--focus"))
            {
                FocusChecks();
                Console.WriteLine($"PASS {checks} focus checks");
                Console.WriteLine(output);
                return 0;
            }
            if (args.Contains("--cover"))
            {
                CoverChecks(context, args.FirstOrDefault(arg => arg.StartsWith("--cover-image="))?[14..]);
                Console.WriteLine($"PASS {checks} cover checks");
                Console.WriteLine(output);
                return 0;
            }
            if (args.Contains("--ux"))
            {
                var uxInstances = new InstanceManager();
                var uxInstance = uxInstances.CreateInstance("Выживание", "1.21.1", LoaderType.Fabric, "0.16.10");
                var uxSecond = uxInstances.CreateInstance("Техномир", "1.20.1", LoaderType.Forge, "47.4.0");
                using var uxHttp = new HttpClient(new CatalogHandler()) { BaseAddress = new("http://localhost") };
                using var uxModel = new LauncherModel(new LauncherSettings { Username = "UX", Language = "ru", Animations = true, DiscordRpc = false, SelectedInstanceId = uxInstance.Id }, uxInstances, new ModrinthClient(uxHttp));
                uxModel.Dispatch = action => context.Post(_ => action(), null);
                using var uxView = new LauncherView(uxModel);
                using var uxHost = new HeadlessHost(uxView.View, 1180, 800);
                uxHost.Render(0); uxHost.Render(1);
                UxChecks(uxModel, uxView, uxHost, context, uxInstance, uxSecond, 1);
                Console.WriteLine($"PASS {checks} UX checks");
                Console.WriteLine(output);
                return 0;
            }
            if (args.Contains("--tlauncher"))
            {
                TLauncherChecks(context);
                Console.WriteLine($"PASS {checks} TLauncher checks");
                Console.WriteLine(output);
                return 0;
            }
            if (args.Contains("--dialogs"))
            {
                DialogChecks(context);
                Console.WriteLine($"PASS {checks} dialog checks");
                Console.WriteLine(output);
                return 0;
            }
            if (args.Contains("--scroll"))
            {
                ScrollChecks();
                Console.WriteLine($"PASS {checks} scroll checks");
                Console.WriteLine(output);
                return 0;
            }
            if (args.Contains("--motion") || args.Contains("--motion-film"))
            {
                var motionInstances = new InstanceManager();
                var motionInstance = motionInstances.CreateInstance("Выживание", "1.21.1", LoaderType.Fabric, "0.16.10");
                motionInstances.CreateInstance("Техномир", "1.20.1", LoaderType.Forge, "47.4.0");
                using var motionModel = new LauncherModel(new LauncherSettings { Username = "Motion", Language = "ru", Animations = true, DiscordRpc = false, SelectedInstanceId = motionInstance.Id }, motionInstances);
                motionModel.Library();
                using var motionView = new LauncherView(motionModel);
                using var motionHost = new HeadlessHost(motionView.View, 1180, 800);
                motionHost.Render(0); motionHost.Render(1);
                if (args.Contains("--motion-film")) MotionFilm(motionModel, motionView, motionHost, context);
                else MotionChecks(motionModel, motionView, motionHost, context, 1);
                Console.WriteLine($"PASS {checks} motion checks");
                Console.WriteLine(output);
                return 0;
            }
            var instances = new InstanceManager();
            var instance = instances.CreateInstance("Выживание", "1.21.1", LoaderType.Fabric, "0.16.10");
            var second = instances.CreateInstance("Техномир", "1.20.1", LoaderType.Forge, "47.4.0");
            using var http = new HttpClient(new CatalogHandler()) { BaseAddress = new("http://localhost") };
            var settings = new LauncherSettings { Username = "TestPlayer", Language = "ru", SelectedInstanceId = instance.Id, DiscordRpc = false };
            settings.Save();
            using var model = new LauncherModel(settings, instances, new ModrinthClient(http));
            model.Dispatch = action => context.Post(_ => action(), null);
            using var presentation = new LauncherView(model);
            using var host = new HeadlessHost(presentation.View, 1180, 800);
            var view = presentation.View;
            double time = 0;
            void Render() { context.Drain(); host.Render(time += 1); host.Render(time += 1); }
            void Click(string name)
            {
                Render();
                var node = view.Find(name);
                Check(node.Get(Ui.Visible) && node.Bounds.Width > 0, "click target " + name);
                host.Click(node.Bounds.X + node.Bounds.Width / 2, node.Bounds.Y + node.Bounds.Height / 2);
                Render();
            }
            void SetField(string name, string text)
            {
                Click(name); host.Key(65, KeyModifiers.Control); host.Text(text); Render();
            }
            Render();
            Capture(host, "home-dark-1180");
            foreach (bool light in new[] { false, true })
            {
                model.LightTheme = light;
                foreach (uint width in new uint[] { 1440, 1180, 1000, 840 })
                {
                    host.Resize(width, 800);
                    foreach (string page in new[] { "home", "instances", "catalog", "downloads", "servers", "account", "settings", "create", "instance" })
                    {
                        if (page == "instance") model.EditInstance(instance.Id); else model.Navigate(page);
                        Render();
                        Check(view.Find("pageTitle").Get(Ui.Text) == model.Title, $"{page} title");
                        Check(view.Root.Bounds.Width <= width + 1, $"{page} width");
                        if (page == "instances")
                            foreach (var action in view.Find("libraryActions").Children)
                                Check(action.Bounds.Width > 0 && action.Bounds.Height > 0 && action.Bounds.X + action.Bounds.Width <= width, "library creation and import actions stay reachable");
                        foreach (var node in view.Interactive().Where(n => Visible(n) && n.Bounds.Width > 0 && n.Bounds.Y < 790))
                        {
                            if (node.Name == "skinCanvas") continue;
                            Check(node.Bounds.X >= -1 && node.Bounds.X + node.Bounds.Width <= width + 1, $"{page} control fits: {node.Name} {node.Get(Ui.Text)} {node.Bounds}");
                        }
                        Check(Contrast(model.Palette.Foreground, model.Palette.Background) >= 4.5, page + " main contrast");
                        Check(Contrast(model.Palette.Muted, model.Palette.Surface) >= 4.5, page + " secondary contrast");
                        Check(Contrast(model.Palette.OnAccent, model.Palette.Accent) >= 4.5, page + " primary button contrast");
                        Capture(host, $"{page}-{(light ? "light" : "dark")}-{width}");
                    }
                }
            }
            foreach (bool light in new[] { false, true })
            {
                model.LightTheme = light;
                foreach (float scale in new[] { 1f, 1.5f, 2f })
                {
                    host.Resize((uint)(840 * scale), (uint)(620 * scale), scale);
                    foreach (string page in new[] { "home", "instances", "catalog" })
                    {
                        model.Navigate(page); Render();
                        string[] targets = page switch
                        {
                            "home" => ["play", "instancePicker", "homeCreate", "launchActions"],
                            "instances" => ["newInstance", "librarySearch", "instanceList"],
                            _ => ["contextInstance", "catalogSearch", "search", "catalogList"]
                        };
                        foreach (string target in targets)
                        {
                            var node = view.Find(target);
                            Check(Visible(node) && node.Bounds.Width > 0 && node.Bounds.Height > 0 && node.Bounds.X >= 0 && node.Bounds.Y >= 0 &&
                                node.Bounds.X + node.Bounds.Width <= 840 && node.Bounds.Y + node.Bounds.Height <= 620, $"{target} fits minimum window at {scale * 100}% DPI");
                        }
                        Capture(host, $"layout-{page}-{(light ? "light" : "dark")}-840x620-{scale * 100:0}pct");
                    }
                }
            }
            host.Resize(1180, 800); model.LightTheme = false; model.Home(); Render();
            Click("navLibrary"); Check(model.Page == "instances", "navigation click");
            SetField("librarySearch", "Техно"); Check(model.LibraryItems.Count == 1 && model.LibraryItems[0].Id == second.Id, "library text binding/filter");
            model.LibraryQuery = ""; model.EditInstance(instance.Id); Render();
            SetField("editName", "Новый мир");
            SetField("maxMemory", "6144");
            Click("saveInstance");
            Check(instances.GetInstance(instance.Id)!.Name == "Новый мир", "save settings from UI");
            Check(instances.GetInstance(instance.Id)!.MaxMemoryMb == 6144, "save memory from UI");
            model.EditMaxMemory = "NaN"; model.SaveInstanceSettings(); context.Drain();
            Check(instances.GetInstance(instance.Id)!.MaxMemoryMb == 6144 && model.Error, "reject invalid memory");
            model.EditMaxMemory = "1024"; model.EditMinMemory = "2048"; model.SaveInstanceSettings(); context.Drain();
            Check(instances.GetInstance(instance.Id)!.MaxMemoryMb == 6144, "reject memory inversion");
            model.EditMaxMemory = "8192"; model.EditMinMemory = "1024";
            var fresh = instances.GetInstance(instance.Id)!; fresh.LastPlayed = new DateTime(2026, 9, 21); instances.SaveInstance(fresh);
            model.SaveInstanceSettings(); context.Drain();
            Check(instances.GetInstance(instance.Id)!.LastPlayed == fresh.LastPlayed, "save preserves external instance changes");
            foreach (var tab in new[] { "screenshots", "crashes", "appearance", "settings" }) { model.SelectInstanceTab(tab); Render(); }
            Check(model.EditName == "Новый мир", "tabs preserve drafts");
            model.Catalog(); Render();
            foreach (string type in new[] { "mod", "modpack", "shader", "resourcepack", "datapack" })
            {
                model.SetContentType(type); Render();
                Check(CatalogHandler.LastSearch.Contains("versions:1.21.1"), type + " minecraft facet");
                Check(type == "mod" ? CatalogHandler.LastSearch.Contains("categories:fabric") : !CatalogHandler.LastSearch.Contains("categories:fabric"), type + " loader facet");
            }
            model.SetContentType("modpack"); Render();
            model.CatalogItems[0].Invoke(); Render();
            Check(model.Page == "project", "open project");
            Check(CatalogHandler.LastVersions.Contains("1.21.1") && !CatalogHandler.LastVersions.Contains("loaders"), "pack version request");
            Check(model.ProjectVersionLabel.StartsWith("matching"), "reject mismatched release returned by server");
            Check(model.CanInstall, "matching pack can install");
            Check(model.DescriptionItems.Count > 0, "native description not blank");
            Capture(host, "project-description");
            model.Catalog(); model.CompatibleOnly = false; Render();
            Check(!CatalogHandler.LastSearch.Contains("versions:"), "unfiltered catalog");
            model.SetInstance(second.Id); model.CompatibleOnly = true; Render();
            Check(CatalogHandler.LastSearch.Contains("versions:1.20.1"), "switch instance filter");
            model.CatalogItems[0].Invoke(); Render();
            Check(!model.CanInstall, "no matching files disables install");
            model.SetInstance(instance.Id); Render();
            Check(model.CanInstall && model.ProjectVersionLabel.StartsWith("matching"), "project reloads when target instance changes");
            model.Catalog(); Render();
            var staleSearch = new TaskCompletionSource<HttpResponseMessage>();
            CatalogHandler.DelayNextSearch = staleSearch;
            model.Query = "old"; model.Search(); Render();
            model.Query = "fresh"; model.Search(); Render();
            staleSearch.SetResult(new(HttpStatusCode.OK) { Content = new StringContent("{\"hits\":[{\"project_id\":\"stale\",\"title\":\"Stale result\"}],\"total_hits\":1}") });
            Render();
            Check(model.CatalogItems.Count == 1 && model.CatalogItems[0].Title == "Better adventures", "late search cannot replace current results");
            model.ShowChoices("Выбор", new[] { new ItemModel { Title = "Первый" }, new ItemModel { Title = "Второй" } }); Render();
            Capture(host, "choice-dialog");
            Check(Visible(view.Find("pages")) && !view.Find("body").Get(Ui.Enabled), "modal preserves the page and blocks its input");
            Click("dialogClose"); Check(!model.DialogOpen, "close dialog");
            model.SelectInstance(); Render(); SetField("choiceSearch", "Техно");
            Check(model.DialogChoices.Count == 1 && model.DialogChoices[0].Id == second.Id, "search instance choices");
            model.CloseDialog(); model.Servers(); model.AddServer(); Render();
            model.AcceptDialog(); Render(); Check(model.DialogOpen && model.DialogError.Length > 0, "server rejects empty name");
            model.DialogFields.Single(f => f.Id == "name").Value = "Test server";
            model.DialogFields.Single(f => f.Id == "address").Value = "localhost:25566";
            model.DialogChoices.Single(c => c.Id == second.Id).Invoke(); Render(); Click("dialogAccept");
            Check(new FavoriteServers().Load().Single().InstanceId == second.Id, "server saves selected instance");
            model.Sessions.Events.SetActive(new() { Ui = new() { ShowInstances = false, AllowInstanceCreate = false, AllowInstanceDelete = false, ShowLogPanel = false } });
            model.Home(); model.Library(); Check(model.Page == "home", "event blocks hidden page routes");
            int jobs = model.Sessions.Downloads.Jobs.Count; model.ImportPack("forbidden.mrpack");
            Check(model.Sessions.Downloads.Jobs.Count == jobs, "event blocks direct pack import");
            model.ToggleLog(); Check(!model.ShowLog, "event blocks game log");
            model.Sessions.Events.Clear();
            string screenshots = Path.Combine(instances.GetGameDir(instance.Id), "screenshots"); Directory.CreateDirectory(screenshots);
            string screenshot = Path.Combine(screenshots, "fixture.png");
            using (var bitmap = new System.Drawing.Bitmap(64, 32))
            {
                using var graphics = System.Drawing.Graphics.FromImage(bitmap); graphics.Clear(System.Drawing.Color.CornflowerBlue);
                bitmap.Save(screenshot, System.Drawing.Imaging.ImageFormat.Png);
            }
            Check(MediaCache.LoadLocal(screenshot) != ImageSource.Empty, "decode local screenshot");
            string broken = Path.Combine(output, "broken.png"); File.WriteAllText(broken, "invalid image");
            Check(MediaCache.LoadLocal(broken) == ImageSource.Empty, "corrupt image has safe fallback");
            model.EditInstance(instance.Id); model.SelectInstanceTab("screenshots"); Render();
            Check(model.DetailItems.Count == 1, "screenshot gallery lists local images");
            model.DetailItems[0].Invoke(); Render(); Capture(host, "screenshot-dialog");
            Check(model.HasDialogImage && model.DialogImage != ImageSource.Empty, "screenshot opens in native dialog");
            model.CloseDialog();
            model.Preferences(); Render(); Click("animations");
            Check(!settings.Animations && view.Animations.ReducedMotion, "disable animation through UI");
            Check(model.CloseToTray && !model.MinimizeToTray, "default window behavior closes to tray and minimizes to taskbar");
            Click("closeToTray"); Click("minimizeToTray");
            var traySettings = LauncherSettings.Load();
            Check(!traySettings.CloseToTray && traySettings.MinimizeToTray, "tray options persist independently from the UI");
            bool exitRequested = false; model.ExitRequested = () => exitRequested = true;
            Click("exitLauncher"); Check(exitRequested, "settings expose an explicit quit action"); model.ExitRequested = null;
            model.CloseToTray = true; model.MinimizeToTray = false;
            Check(JsonSerializer.Deserialize<LauncherSettings>("{\"closeOnLaunch\":true}") is { CloseToTray: true, MinimizeToTray: false, CloseOnLaunch: true }, "old settings gain tray defaults without changing game startup preference");
            model.Home(); Render(); Check(!view.Animations.NeedsFrames, "reduced motion settles");
            model.Animations = true; model.Library(); host.Render(time += 1);
            host.Render(time += 1); Check(!view.Animations.NeedsFrames, "animation settles to idle");
            model.OpenSkinEditor(); Render(); Capture(host, "skin-editor");
            model.LoadSkinBytes(File.ReadAllBytes(screenshot)); Render();
            var skinCanvas = view.Find("skinCanvas").Bounds;
            model.SkinColor = "#FF0000"; host.Click(skinCanvas.X + 4, skinCanvas.Y + 4); Render();
            using (var stream = new MemoryStream(model.EncodeSkin())) using (var bitmap = new System.Drawing.Bitmap(stream))
                Check(bitmap.GetPixel(0, 0).ToArgb() == System.Drawing.Color.Red.ToArgb(), "skin pencil paints pixel");
            Click("undoSkin");
            using (var stream = new MemoryStream(model.EncodeSkin())) using (var bitmap = new System.Drawing.Bitmap(stream))
                Check(bitmap.GetPixel(0, 0).ToArgb() == System.Drawing.Color.CornflowerBlue.ToArgb() && bitmap.GetPixel(0, 63).A == 0, "skin undo and legacy dimensions");
            model.SkinFill = true; host.Click(skinCanvas.X + 4, skinCanvas.Y + 4); Render();
            using (var stream = new MemoryStream(model.EncodeSkin())) using (var bitmap = new System.Drawing.Bitmap(stream))
                Check(bitmap.GetPixel(63, 31).ToArgb() == System.Drawing.Color.Red.ToArgb() && bitmap.GetPixel(0, 32).A == 0, "skin fill stays inside connected area");
            model.SkinFill = false;
            model.ChooseLanguage(); model.DialogChoices.Single(c => c.Title == "English").Invoke();
            host.Resize(1260, 1200, 1.5f); model.Preferences(); Render(); Capture(host, "settings-en-150dpi");
            Check(view.Root.Bounds.Width <= 841 && view.Find("pageTitle").Get(Ui.Text) == "Settings", "English layout at 150 percent DPI");
            host.Key(9); Render(); Check(host.FocusedElement != null, "keyboard tab focus");
            using (var lifetime = new CancellationTokenSource())
            {
                var shutdownContext = new LauncherSynchronizationContext(new WindowDispatcher(), lifetime.Token);
                bool invoked = false; shutdownContext.Post(_ => invoked = true, null); lifetime.Cancel(); shutdownContext.Post(_ => invoked = true, null);
                Check(!invoked, "shutdown context drops callbacks after window stop");
            }
            GameLifecycle(model, context);
            host.Resize(1180, 800); model.Downloads(); Render();
            int completed = model.DownloadItems.ToList().FindIndex(i => i.Primary.Length == 0 && i.Secondary.Length > 0);
            Check(completed >= 0, "completed download retains instance action");
            UiVirtualList.ScrollIntoView(view.Find("downloadList"), completed); Render();
            var openInstance = view.Interactive().FirstOrDefault(n => Visible(n) && n.Get(Ui.Text) == "Open instance" && n.Bounds.Y > 0 && n.Bounds.Y + n.Bounds.Height < 800);
            Check(openInstance != null, "completed download action is visible");
            host.Click(openInstance!.Bounds.X + 20, openInstance.Bounds.Y + 15); Render();
            Check(model.Page == "instance", "completed download opens instance settings");
            time = UxChecks(model, presentation, host, context, instance, second, time);
            MotionChecks(model, presentation, host, context, time);
            FocusChecks();
            CoverChecks(context);
            ScrollChecks();
            DialogChecks(context);
            TLauncherChecks(context);
            InstalledContentChecks(context);
            ServerSyncChecks(context);
            ServerDiscoveryChecks(context);
            ModUpdateChecks(context);
            GameBridgeLifecycleChecks(context);
            ServerSyncSettingsChecks(context);
            BundledBridgeChecks(context);
            Console.WriteLine($"PASS {checks} checks");
            Console.WriteLine(output);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); Console.WriteLine(output); return 1; }
    }
    private static bool Visible(Element node)
    {
        for (Element? e = node; e != null; e = e.Parent) if (!e.Get(Ui.Visible)) return false;
        return true;
    }
    private static void Check(bool result, string message)
    {
        if (!result) throw new InvalidOperationException("FAIL " + message);
        checks++;
    }
    private static void GameLifecycle(LauncherModel model, Pump context)
    {
        var java = Path.Combine(AppContext.BaseDirectory, "stub", "bin", "java.exe");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "stub", "release"), "JAVA_VERSION=\"21\"\n");
        var instance = model.Instances.CreateInstance("Lifecycle fixture", "fixture-1");
        instance.JavaPath = java; model.Instances.SaveInstance(instance);
        string sharedVersion = Path.Combine(model.Instances.SharedDir, "versions", instance.McVersion);
        Directory.CreateDirectory(sharedVersion);
        File.WriteAllText(Path.Combine(sharedVersion, instance.McVersion + ".json"), JsonSerializer.Serialize(new VersionMeta
        {
            Id = instance.McVersion, MainClass = "MechanicaFixture", MinecraftArguments = "",
            JavaVersion = new() { MajorVersion = 21, Component = "fixture" }
        }));
        string gameDir = model.Instances.GetGameDir(instance.Id), gameVersion = Path.Combine(gameDir, "versions", instance.McVersion);
        Directory.CreateDirectory(gameVersion); File.WriteAllBytes(Path.Combine(gameVersion, instance.McVersion + ".jar"), []);
        void Until(Func<bool> condition)
        {
            var start = System.Diagnostics.Stopwatch.StartNew();
            while (!condition())
            {
                if (start.Elapsed.TotalSeconds > 12) throw new TimeoutException("Game lifecycle fixture timed out.");
                context.Drain(); Thread.Sleep(10);
            }
            context.Drain();
        }
        void Await(Task task) { Until(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
        Await(model.Sessions.LaunchAsync(instance, null, null, _ => Task.FromResult(true)));
        Check(model.Sessions.IsRunning(instance.Id), "launch starts tracked process");
        Until(() => model.Sessions.RunningCount == 0);
        Check(CrashAnalyzer.GetReports(gameDir).Count == 0, "clean game exit is not a crash");
        Check(model.Instances.GetInstance(instance.Id)!.LastPlayed != null, "launch persists last played");
        Check(!model.Sessions.Log.Contains("fixture-secret-token"), "live log redacts tokens");
        Check(!File.ReadAllText(Path.Combine(gameDir, "logs", "launcher-latest.log")).Contains("fixture-secret-token"), "persisted log redacts tokens");
        instance.JvmArgs = "--fixture-crash";
        Await(model.Sessions.LaunchAsync(instance, null, null, _ => Task.FromResult(true)));
        Until(() => model.Sessions.RunningCount == 0);
        Check(CrashAnalyzer.GetReports(gameDir).Any(r => r.Reason == "memory"), "unexpected exit captures readable crash report");
        instance.JvmArgs = "--fixture-wait";
        Await(model.Sessions.LaunchAsync(instance, null, null, _ => Task.FromResult(true)));
        int count = CrashAnalyzer.GetReports(gameDir).Count;
        model.Sessions.Stop(instance.Id);
        Until(() => model.Sessions.RunningCount == 0);
        Check(CrashAnalyzer.GetReports(gameDir).Count == count, "user stop is not a crash");
        var pending = model.Sessions.Enqueue("Cancellation fixture", instance.Id, token => Task.Delay(Timeout.Infinite, token));
        Until(() => model.Sessions.Preparing);
        Check(model.Sessions.IsBusy(instance.Id), "queue reserves instance");
        model.Sessions.Cancel(); Await(pending.Completion);
        Until(() => !model.Sessions.Preparing);
        Check(pending.State == DownloadState.Cancelled, "cancel releases queue gate");
        int attempts = 0;
        var retry = model.Sessions.Enqueue("Retry fixture", null, _ => ++attempts == 1 ? Task.FromException(new IOException("fixture network failure")) : Task.CompletedTask);
        Await(retry.Completion); Check(retry.State == DownloadState.Failed, "queue captures failure");
        model.Sessions.Downloads.Retry(retry);
        Until(() => attempts == 2 && !model.Sessions.Downloads.HasPending);
        Check(model.Sessions.Downloads.Jobs.Last().State == DownloadState.Completed, "queue retry succeeds");
    }
    private static double Contrast(Color foreground, Color background)
    {
        double a = foreground.R * .2126 + foreground.G * .7152 + foreground.B * .0722;
        double b = background.R * .2126 + background.G * .7152 + background.B * .0722;
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }
    private static void Capture(HeadlessHost host, string name)
    {
        byte[] pixels = host.ReadPixels();
        for (int i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
        using var bitmap = new System.Drawing.Bitmap((int)host.Width, (int)host.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var bits = bitmap.LockBits(new(0, 0, bitmap.Width, bitmap.Height), System.Drawing.Imaging.ImageLockMode.WriteOnly, bitmap.PixelFormat);
        try { Marshal.Copy(pixels, 0, bits.Scan0, pixels.Length); }
        finally { bitmap.UnlockBits(bits); }
        bitmap.Save(Path.Combine(output, name + ".png"));
    }
    private sealed class Pump : SynchronizationContext, IDisposable
    {
        private readonly ConcurrentQueue<(SendOrPostCallback callback, object? state)> queue = new();
        public override void Post(SendOrPostCallback d, object? state) => queue.Enqueue((d, state));
        public void Drain() { while (queue.TryDequeue(out var item)) item.callback(item.state); }
        public void Dispose() { Drain(); SetSynchronizationContext(null); }
    }
    private sealed class CatalogHandler : HttpMessageHandler
    {
        public static string LastSearch = "", LastVersions = "";
        public static int SearchRequests;
        public static HttpResponseMessage? NextSearch, NextProject, NextVersions;
        public static TaskCompletionSource<HttpResponseMessage>? DelayNextSearch;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            object value;
            if (path.EndsWith("/search"))
            {
                SearchRequests++;
                LastSearch = Uri.UnescapeDataString(request.RequestUri.Query);
                if (Interlocked.Exchange(ref DelayNextSearch, null) is { } delayed) return delayed.Task;
                if (Interlocked.Exchange(ref NextSearch, null) is { } searchResponse) return Task.FromResult(searchResponse);
                value = new ModrinthSearchResult { TotalHits = 1, Hits = [new() { ProjectId = "fixture", Slug = "fixture", Title = "Better adventures", Description = "Исследуй мир, строй и играй вместе с друзьями.", Author = "Modrinth", Downloads = 140000 }] };
            }
            else if (path.EndsWith("/version"))
            {
                LastVersions = Uri.UnescapeDataString(request.RequestUri.Query);
                if (Interlocked.Exchange(ref NextVersions, null) is { } versionResponse) return Task.FromResult(versionResponse);
                value = new ModrinthVersion[]
                {
                    new() { Id = "beta", VersionNumber = "preview", VersionType = "beta", GameVersions = ["1.21.1"], Loaders = ["forge"], Files = [new() { Filename = "preview.mrpack" }] },
                    new() { Id = "wrong", VersionNumber = "wrong", Name = "Wrong release", GameVersions = ["1.22"], Loaders = ["forge"], Files = [new() { Filename = "wrong.mrpack" }] },
                    new() { Id = "right", VersionNumber = "matching", Name = "Matching release", GameVersions = ["1.21.1"], Loaders = ["forge"], Files = [new() { Filename = "matching.mrpack" }] }
                };
            }
            else
            {
                if (Interlocked.Exchange(ref NextProject, null) is { } projectResponse) return Task.FromResult(projectResponse);
                value = new ModrinthProjectInfo { Id = "fixture", Title = "Better adventures", Description = "Исследуй мир.", Body = "# Новый мир\n\nМоды, приключения и строительство.\n\n## Возможности\n\n- Новые биомы\n- Быстрая загрузка\n\n[Документация](https://modrinth.com)" };
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") });
        }
    }
}
