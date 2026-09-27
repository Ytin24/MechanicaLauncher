using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MechanicaLauncher.Core.Instances;
using MechanicaLauncher.Core.Localization;
using MechanicaLauncher.Core.Profiles;
using MechanicaLauncher.Desktop;
using Nitidus;
using Nitidus.Native;

internal static partial class Program
{
    private static void ServerSyncSettingsChecks(Pump context)
    {
        string? previousData = Environment.GetEnvironmentVariable("MECHANICA_DATA_DIR");
        string previousLanguage = Locale.CurrentLanguage;
        string data = Path.Combine(output, "server-sync-settings-data");
        Environment.SetEnvironmentVariable("MECHANICA_DATA_DIR", data);
        try
        {
            var legacy = JsonSerializer.Deserialize<GameInstance>("{\"id\":\"legacy\",\"name\":\"Legacy\",\"mcVersion\":\"1.21.1\",\"loader\":\"Fabric\",\"loaderVersion\":\"0.19.3\"}")!;
            Check(!new GameInstance().UseServerModSync && !legacy.UseServerModSync, "server mod sync requires explicit opt-in for new and existing instance files");
            var instances = new InstanceManager(data);
            var first = instances.CreateInstance("Сборка для сервера", "1.21.1", LoaderType.Fabric, "0.19.3");
            var second = instances.CreateInstance("Личная сборка", "1.21.1", LoaderType.Fabric, "0.19.3");
            var vanilla = instances.CreateInstance("Без модов", "1.21.1");
            using var model = new LauncherModel(new LauncherSettings
            {
                Language = "ru", Username = "SyncSettings", DiscordRpc = false, Animations = false, SelectedInstanceId = first.Id
            }, instances);
            model.Dispatch = action => context.Post(_ => action(), null);
            using var presentation = new LauncherView(model);
            using var host = new HeadlessHost(presentation.View, 1180, 800);
            var view = presentation.View;
            var scroll = view.Find("pageScroll");
            var toggle = view.Find("useServerModSync");
            var save = view.Find("saveInstance");
            double time = 0;
            void Render() { context.Drain(); host.Render(time += 1); host.Render(time += 1); }
            void Until(Func<bool> done, string operation)
            {
                var timer = Stopwatch.StartNew();
                while (!done())
                {
                    if (timer.Elapsed.TotalSeconds > 10) throw new TimeoutException("Server sync settings: " + operation + "; " + model.Message);
                    context.Drain(); Thread.Sleep(1);
                }
                Render();
            }
            bool Saved(string id) => new InstanceManager(data).GetInstance(id)!.UseServerModSync;
            bool Fits(Element node, Rect area)
            {
                var box = UiTransform.VisualBounds(node);
                return box.Width > 0 && box.Height > 0 && box.X >= area.X - 1 && box.Y >= area.Y - 1 &&
                    box.X + box.Width <= area.X + area.Width + 1 && box.Y + box.Height <= area.Y + area.Height + 1;
            }
            void Reveal(Element node)
            {
                var info = UiScroll.Inspect(scroll);
                var box = UiTransform.VisualBounds(node);
                double offset = info.VerticalOffset;
                if (box.Y < info.Viewport.Y) offset += box.Y - info.Viewport.Y;
                else if (box.Y + box.Height > info.Viewport.Y + info.Viewport.Height) offset += box.Y + box.Height - info.Viewport.Y - info.Viewport.Height;
                UiScroll.ScrollTo(scroll, 0, offset); Render();
                Check(Fits(node, UiScroll.Inspect(scroll).Viewport), "server sync settings control is reachable by scrolling");
            }
            void Click(Element node)
            {
                Reveal(node);
                var box = UiTransform.VisualBounds(node);
                host.Click(box.X + box.Width / 2, box.Y + box.Height / 2); Render();
            }

            model.EditInstance(first.Id); Render();
            Check(!model.EditUseServerModSync && model.CanEditServerModSync && toggle.Get(Ui.Enabled), "modded instance exposes an unchecked server sync option");
            Click(toggle);
            Check(model.EditUseServerModSync && !Saved(first.Id), "native checkbox changes the draft without saving immediately");
            Click(save);
            Check(Saved(first.Id) && !Saved(second.Id), "saving enables synchronization for only the edited instance");
            using (var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(instances.GetInstanceDir(first.Id), "instance.json"))))
                Check(json.RootElement.GetProperty("useServerModSync").ValueKind == JsonValueKind.True, "the opt-in is persisted as a JSON boolean");
            model.EditInstance(second.Id); Render();
            Check(!model.EditUseServerModSync, "opening another instance does not carry over the previous draft");
            model.EditUseServerModSync = true;
            model.EditInstance(first.Id); Render();
            Check(model.EditUseServerModSync && !Saved(second.Id), "changing the editor discards an unsaved option in another instance");

            foreach (bool light in new[] { false, true })
            foreach (var size in new[] { (1180u, 800u), (840u, 620u) })
            {
                model.LightTheme = light; host.Resize(size.Item1, size.Item2); model.DismissMessage();
                model.EditInstance(first.Id); UiScroll.ToTop(scroll); Render();
                var viewport = UiScroll.Inspect(scroll).Viewport;
                Check(toggle.Get(Ui.Text) == "Mechanica Server Sync" && toggle.Get(Ui.Enabled) && Fits(toggle, viewport), "server sync checkbox fits the regular instance form");
                Check(Fits(save, viewport), "server sync option does not push the regular save action below the viewport");
                foreach (var action in save.Parent!.Children.Where(node => node.Type.Is(UiTypes.Button) && Visible(node)))
                    Check(Fits(action, viewport), "all regular instance footer actions remain visible after adding server sync");
                Capture(host, $"server-sync-settings-{(light ? "light" : "dark")}-{size.Item1}x{size.Item2}");
                Click(toggle); Click(save);
                Check(!Saved(first.Id), "native checkbox can disable and persist server sync at every supported size and theme");
                model.EditInstance(first.Id); Render();
                Check(!model.EditUseServerModSync, "disabled server sync reloads from the instance file");
                Click(toggle); Click(save);
                Check(Saved(first.Id), "server sync can be explicitly enabled again");
                model.ToggleAdvanced(); Render(); Reveal(save);
                foreach (var action in save.Parent!.Children.Where(node => node.Type.Is(UiTypes.Button) && Visible(node)))
                    Check(Fits(action, UiScroll.Inspect(scroll).Viewport), "advanced instance footer remains reachable with server sync enabled");
                Capture(host, $"server-sync-settings-advanced-{(light ? "light" : "dark")}-{size.Item1}x{size.Item2}");
            }

            model.EditInstance(vanilla.Id); Render();
            Check(!model.CanEditServerModSync && !toggle.Get(Ui.Enabled) && !model.EditUseServerModSync, "Vanilla does not advertise a usable mod bridge");
            Click(toggle);
            Check(!model.EditUseServerModSync && !Saved(vanilla.Id), "disabled Vanilla checkbox cannot opt into the mod bridge");
            model.EditUseServerModSync = true; model.SaveInstanceSettings(); Render();
            Check(!Saved(vanilla.Id), "save also rejects a programmatically enabled bridge for Vanilla");

            model.EditInstance(first.Id); Render();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var job = model.Sessions.Enqueue("Settings busy fixture", first.Id, async token =>
            {
                entered.TrySetResult(); await release.Task.WaitAsync(token);
            });
            try
            {
                Until(() => entered.Task.IsCompleted && model.Sessions.IsBusy(first.Id), "reserving the instance");
                Check(!model.CanEditServerModSync && !toggle.Get(Ui.Enabled), "an active instance job disables its server sync checkbox");
                model.EditUseServerModSync = false; model.SaveInstanceSettings(); Render();
                Check(model.Error && Saved(first.Id), "the existing busy guard prevents saving the opt-in while the instance is in use");
            }
            finally
            {
                release.TrySetResult(); Until(() => job.Completion.IsCompleted, "releasing the instance reservation");
                job.Completion.GetAwaiter().GetResult();
            }
            Until(() => model.CanEditServerModSync, "reenabling the option after the job");
            model.SaveInstanceSettings(); Render();
            Check(!Saved(first.Id) && !Saved(second.Id) && !model.Error, "settings save succeeds after the instance becomes idle");
        }
        finally { Environment.SetEnvironmentVariable("MECHANICA_DATA_DIR", previousData); Locale.Init(previousLanguage); }
    }
}
