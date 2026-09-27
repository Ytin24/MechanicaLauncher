using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MechanicaLauncher.Core.Instances;
using MechanicaLauncher.Core.Localization;
using MechanicaLauncher.Core.Profiles;
using MechanicaLauncher.Core.Servers;
using MechanicaLauncher.Desktop;
using Nitidus;
using Nitidus.Native;

internal static partial class Program
{
    private static void ServerSyncChecks(Pump context)
    {
        string? previousData = Environment.GetEnvironmentVariable("MECHANICA_DATA_DIR");
        string previousLanguage = Locale.CurrentLanguage;
        string data = Path.Combine(output, "server-sync-ui-data");
        Environment.SetEnvironmentVariable("MECHANICA_DATA_DIR", data);
        try
        {
            var instances = new InstanceManager(data);
            var first = instances.CreateInstance("Сборка друзей", "1.21.1", LoaderType.Fabric, "0.19.3");
            var second = instances.CreateInstance("Другая сборка", "1.21.1", LoaderType.Fabric, "0.19.3");
            first.UseServerModSync = true; instances.SaveInstance(first);
            second.UseServerModSync = true; instances.SaveInstance(second);
            using var model = new LauncherModel(new LauncherSettings
            {
                Language = "ru", Username = "ServerUi", DiscordRpc = false, Animations = false, SelectedInstanceId = first.Id
            }, instances);
            model.Dispatch = action => context.Post(_ => action(), null);
            using var presentation = new LauncherView(model);
            using var host = new HeadlessHost(presentation.View, 1180, 800);
            var view = presentation.View;
            var store = new FavoriteServers(data);
            double time = 0;
            void Render() { context.Drain(); host.Render(time += 1); host.Render(time += 1); }
            void Until(Func<bool> done, string operation)
            {
                var timer = Stopwatch.StartNew();
                while (!done())
                {
                    if (timer.Elapsed.TotalSeconds > 10) throw new TimeoutException("Server UI: " + operation + "; " + model.DialogError);
                    context.Drain(); Thread.Sleep(1);
                }
                Render();
            }
            void Await(Task task, string operation) { Until(() => task.IsCompleted, operation); task.GetAwaiter().GetResult(); }
            FieldModel Field(string id) => model.DialogFields.Single(field => field.Id == id);
            void Edit() { model.RefreshServers(); model.ServerItems.Single().Invoke2(); Render(); Check(model.DialogOpen, "favorite server editor opens"); }
            void Save() { model.AcceptDialog(); Until(() => !model.DialogOpen || model.DialogError.Length > 0, "saving server"); Check(!model.DialogOpen, "valid server settings save: " + model.DialogError); }
            bool Fits(Element node, Rect area)
            {
                var box = UiTransform.VisualBounds(node);
                return box.Width > 0 && box.Height > 0 && box.X >= area.X - 1 && box.Y >= area.Y - 1 &&
                    box.X + box.Width <= area.X + area.Width + 1 && box.Y + box.Height <= area.Y + area.Height + 1;
            }
            void Reveal(Element node)
            {
                var scroll = view.Find("dialogScroll");
                var info = UiScroll.Inspect(scroll);
                var box = UiTransform.VisualBounds(node);
                double offset = info.VerticalOffset;
                if (box.Y < info.Viewport.Y) offset += box.Y - info.Viewport.Y;
                else if (box.Y + box.Height > info.Viewport.Y + info.Viewport.Height) offset += box.Y + box.Height - info.Viewport.Y - info.Viewport.Height;
                UiScroll.ScrollTo(scroll, 0, offset); Render();
                Check(Fits(node, UiScroll.Inspect(scroll).Viewport), "server editor control can be revealed by scrolling");
            }

            model.Servers(); model.AddServer(); Render();
            Check(Field("syncUrl").Value.Length == 0 && Field("autoSync").IsToggle && !Field("autoSync").IsChecked, "new server has optional mod source and opt-in automatic downloads");
            Field("name").Value = "Сервер друзей"; Field("address").Value = "play.example.com:25566";
            Field("syncUrl").Value = "https://mods.example.com/mechanica/descriptor.json";
            Field("autoSync").IsChecked = true; Save();
            var saved = store.Load().Single();
            Check(saved.Host == "play.example.com" && saved.Port == 25566 && saved.InstanceId == first.Id && saved.AutoSync && !saved.AllowLocalSync,
                "server saves the target and explicit automatic-download choice");
            Check(saved.SyncManifestUrl == "https://mods.example.com/mechanica/descriptor.json", "server preserves the HTTPS mod list URL");
            Edit(); Field("name").Value = "Друзья";
            Check(Field("autoSync").IsChecked, "renaming a server keeps its existing download permission");
            Field("address").Value = "other.example.com:25567";
            Check(!Field("autoSync").IsChecked, "changing the host or port clears inherited automatic-download permission");
            Field("autoSync").IsChecked = true;
            Field("syncUrl").Value = "https://mods.example.com/another.json";
            Check(!Field("autoSync").IsChecked, "changing the mod source clears inherited permission");
            Field("autoSync").IsChecked = true;
            model.DialogChoices.Single(item => item.Id == second.Id).Invoke();
            Check(!Field("autoSync").IsChecked, "changing the instance clears inherited permission");
            Field("autoSync").IsChecked = true; Save();
            saved = store.Load().Single();
            Check(saved.InstanceId == second.Id && saved.AutoSync && saved.Host == "other.example.com", "explicit permission after editing the destination is saved");

            Edit(); Field("syncUrl").Value = "http://external.example.com/descriptor.json";
            model.AcceptDialog(); Render();
            Check(model.DialogOpen && model.DialogError.Contains("HTTPS") && store.Load().Single() == saved, "external HTTP is rejected without changing the stored server");
            Check(Fits(view.Find("dialogAccept"), view.Find("dialog").Bounds), "invalid URL leaves the save action reachable");
            Field("syncUrl").Value = "http://127.0.0.1:28081/mechanica/descriptor.json";
            Field("autoSync").IsChecked = true; Save();
            saved = store.Load().Single();
            Check(saved.AllowLocalSync && saved.AutoSync, "explicit loopback HTTP is saved with the local-source opt-in");

            foreach (bool light in new[] { false, true })
            foreach (var size in new[] { (1180u, 800u), (840u, 620u) })
            {
                model.LightTheme = light; host.Resize(size.Item1, size.Item2); Edit();
                UiScroll.ToTop(view.Find("dialogScroll")); Render();
                Check(Fits(view.Find("dialog"), new(0, 0, size.Item1, size.Item2)), "server form fits supported window sizes");
                Check(Fits(view.Find("dialogAccept"), view.Find("dialog").Bounds) && Fits(view.Find("dialogCancel"), view.Find("dialog").Bounds), "server form keeps both footer actions visible");
                Capture(host, $"server-sync-editor-{(light ? "light" : "dark")}-{size.Item1}x{size.Item2}");
                var fields = view.Find("dialogFields");
                for (int index = 0; index < model.DialogFields.Count; index++)
                {
                    UiVirtualList.ScrollIntoView(fields, index, ScrollAlignment.Start); Render();
                    var row = UiVirtualList.GetRealizedItem(fields, index)!;
                    var control = row.DescendantsAndSelf().First(node => node.Type.Is(UiTypes.TextField) && Visible(node) ||
                        node.Get(Ui.Text) == Field("autoSync").Label && view.Interactive().Contains(node));
                    Reveal(control);
                    if (model.DialogFields[index].IsToggle)
                    {
                        bool before = Field("autoSync").IsChecked;
                        var bounds = UiTransform.VisualBounds(control);
                        host.Click(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2); Render();
                        Check(Field("autoSync").IsChecked != before, "native automatic-download checkbox updates its field binding");
                        host.Click(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2); Render();
                        Check(Field("autoSync").IsChecked == before, "native checkbox can restore the explicit choice");
                    }
                }
                UiScroll.ScrollTo(view.Find("dialogScroll"), 0, 10000); Render();
                UiVirtualList.ScrollIntoView(view.Find("choices"), 1, ScrollAlignment.Start); Render();
                var choice = UiVirtualList.GetRealizedItem(view.Find("choices"), 1)!;
                var button = choice.DescendantsAndSelf().First(node => node.Type.Is(UiTypes.Button));
                Check(Fits(button, UiScroll.Inspect(view.Find("dialogScroll")).Viewport), "instance selection stays accessible below the server fields");
                Capture(host, $"server-sync-editor-bottom-{(light ? "light" : "dark")}-{size.Item1}x{size.Item2}");
                model.CloseDialog(); Render();
            }

            using var handler = new ServerSyncUiHandler();
            using var http = new HttpClient(handler);
            var sync = new ServerModSync(http, approvedExternalOrigins: [new Uri("https://sync-ui.example")]);
            var planTask = sync.PlanAsync(new Uri("https://sync-ui.example/descriptor.json"), second, instances.GetGameDir(second.Id));
            Await(planTask, "building a real server change plan against mock HTTP");
            var plan = planTask.Result;
            Check(plan.Changes.Count == 1 && plan.DownloadBytes == 4096, "confirmation uses an actual validated server plan");
            int attention = 0, attentionThread = 0, uiThread = Environment.CurrentManagedThreadId;
            model.AttentionRequested = () => { attention++; attentionThread = Environment.CurrentManagedThreadId; };
            foreach (bool light in new[] { false, true })
            foreach (var size in new[] { (1180u, 800u), (840u, 620u) })
            {
                model.LightTheme = light; host.Resize(size.Item1, size.Item2);
                int before = attention;
                var prompt = Task.Run(() => model.ConfirmServerSyncOnUi(saved, plan));
                Until(() => model.DialogOpen, "dispatching server confirmation");
                Check(attention == before + 1 && attentionThread == uiThread, "background server confirmation restores attention on the UI thread");
                Check(model.DialogBody.Contains(saved.Name) && model.DialogBody.Contains(second.Name) && model.DialogBody.Contains("Добавить: 1") &&
                    model.DialogBody.Contains("Скачать:"), "server confirmation identifies the destination, changes and download size");
                Check(Fits(view.Find("dialogAccept"), view.Find("dialog").Bounds) && Fits(view.Find("dialogCancel"), view.Find("dialog").Bounds), "server sync confirmation keeps its actions visible");
                Capture(host, $"server-sync-confirm-{(light ? "light" : "dark")}-{size.Item1}x{size.Item2}");
                model.CloseDialog(); Await(prompt, "cancelling server confirmation");
                Check(!prompt.Result, "closing server confirmation declines the update");
            }
            var accepted = Task.Run(() => model.ConfirmServerSyncOnUi(saved, plan));
            Until(() => model.DialogOpen, "opening accepted confirmation"); model.AcceptDialog(); Await(accepted, "accepting server confirmation");
            Check(accepted.Result && !model.DialogOpen, "explicit confirmation accepts once and closes the modal");
            using (var cancellation = new CancellationTokenSource())
            {
                var cancelled = model.ConfirmServerSyncOnUi(saved, plan, cancellation.Token);
                Until(() => model.DialogOpen, "opening cancellable confirmation");
                cancellation.Cancel(); Await(cancelled, "cancelling pending consent");
                Check(!cancelled.Result && !model.DialogOpen, "cancelling server preparation dismisses its own consent dialog");
            }
            using (var cancellation = new CancellationTokenSource())
            {
                int before = attention;
                var cancelled = model.ConfirmServerSyncOnUi(saved, plan, cancellation.Token);
                cancellation.Cancel(); Await(cancelled, "cancelling before UI dispatch");
                Check(!cancelled.Result && !model.DialogOpen && attention == before, "cancelled queued consent never opens or restores the window");
            }
            using (var cancellation = new CancellationTokenSource())
            {
                var replaced = model.ConfirmServerSyncOnUi(saved, plan, cancellation.Token);
                Until(() => model.DialogOpen, "opening consent before replacement");
                model.CloseDialog(); model.BeginDialog("Другой диалог", "", "", null);
                cancellation.Cancel(); Await(replaced, "cancelling replaced consent");
                Check(!replaced.Result && model.DialogOpen && model.DialogTitle == "Другой диалог", "late cancellation never closes another dialog generation");
                model.CloseDialog();
            }
            model.BeginDialog("Текущий диалог", "", "", null);
            int attentionBefore = attention;
            var busyPrompt = Task.Run(() => model.ConfirmCompatibilityOnUi("Compatibility")); Await(busyPrompt, "rejecting an overlapping prompt");
            Check(!busyPrompt.Result && model.DialogTitle == "Текущий диалог" && attention == attentionBefore, "compatibility prompt never replaces an existing modal or steals attention");
            model.CloseDialog(); model.BeginTLauncherCheck();
            var blocked = model.ConfirmServerSyncOnUi(saved, plan); Await(blocked, "blocked confirmation");
            Check(!blocked.Result && !model.DialogOpen && attention == attentionBefore, "TLauncher gate declines server synchronization without opening a dialog");

            var queued = new ConcurrentQueue<Action>();
            var closing = new LauncherModel(new LauncherSettings { Language = "ru", DiscordRpc = false, SelectedInstanceId = first.Id }, instances);
            closing.Dispatch = queued.Enqueue;
            var pending = closing.ConfirmCompatibilityOnUi("Compatibility");
            Check(!pending.IsCompleted && !closing.DialogOpen, "a background confirmation waits for UI dispatch");
            closing.Dispose(); Await(pending, "disposing a queued confirmation");
            while (queued.TryDequeue(out var action)) action();
            Check(!pending.Result && !closing.DialogOpen && !closing.ConfirmCompatibilityOnUi("After dispose").Result, "disposing the model declines queued and future confirmations");
            Check(handler.Requests == 2, "server UI tests used only the strict descriptor and manifest fixture endpoints");
        }
        finally { Environment.SetEnvironmentVariable("MECHANICA_DATA_DIR", previousData); Locale.Init(previousLanguage); }
    }

    private sealed class ServerSyncUiHandler : HttpMessageHandler
    {
        private readonly byte[] manifest, descriptor;
        public int Requests;
        public ServerSyncUiHandler()
        {
            var serverId = Guid.NewGuid();
            manifest = JsonSerializer.SerializeToUtf8Bytes(new
            {
                type = "Manifest", protocolMajor = 1, protocolMinor = 0, serverId, revision = 1, expiresUtc = DateTime.UtcNow.AddHours(1),
                targetId = "fabric-1.21.1", minecraft = "1.21.1", loader = "fabric", loaderVersion = "0.19.3",
                files = new[] { new { artifactId = "ui-fixture", modIds = new[] { "ui_fixture" }, filename = "ui-fixture.jar", client = "required",
                    size = 4096, sha512 = new string('a', 128), dependencies = Array.Empty<string>(), source = new { type = "external", url = "https://sync-ui.example/ui-fixture.jar" } } }
            });
            descriptor = JsonSerializer.SerializeToUtf8Bytes(new
            {
                descriptorVersion = 1, serverId, protocols = new[] { new { major = 1, minor = 0, requiredCapabilities = new[] { "mods-v1" },
                    targets = new[] { new { targetId = "fabric-1.21.1", minecraft = "1.21.1", loader = "fabric", loaderVersion = "0.19.3",
                        manifestUrl = "https://sync-ui.example/manifest.json", sha512 = Convert.ToHexString(SHA512.HashData(manifest)).ToLowerInvariant() } } } }
            });
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            byte[] bytes = request.RequestUri!.AbsolutePath switch
            {
                "/descriptor.json" => descriptor, "/manifest.json" => manifest,
                _ => throw new InvalidOperationException("Unexpected server UI HTTP request: " + request.RequestUri)
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
}
