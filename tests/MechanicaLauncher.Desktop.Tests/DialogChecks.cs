using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MechanicaLauncher.Core.Instances;
using MechanicaLauncher.Core.Profiles;
using MechanicaLauncher.Desktop;
using Nitidus;
using Nitidus.Native;

internal static partial class Program
{
    private static void DialogChecks(Pump context)
    {
        var instances = new InstanceManager();
        var instance = instances.CreateInstance("Окно диалога", "1.21.1");
        using var model = new LauncherModel(new LauncherSettings { Username = "DialogTest", Language = "ru", SelectedInstanceId = instance.Id, DiscordRpc = false, Animations = false }, instances);
        using var presentation = new LauncherView(model);
        using var host = new HeadlessHost(presentation.View, 840, 620);
        var view = presentation.View;
        double time = 0;
        void Render() { context.Drain(); host.Render(time += 1); host.Render(time += 1); }
        void Click(Element node) => host.Click(node.Bounds.X + node.Bounds.Width / 2, node.Bounds.Y + node.Bounds.Height / 2);
        bool Inside(Element node, Element parent)
        {
            var a = node.Bounds; var b = parent.Bounds;
            return a.Width > 0 && a.Height > 0 && a.X >= b.X && a.Y >= b.Y && a.X + a.Width <= b.X + b.Width + 1 && a.Y + a.Height <= b.Y + b.Height + 1;
        }
        bool InDialog(Element? node)
        {
            for (; node != null; node = node.Parent) if (node.Name == "dialog") return true;
            return false;
        }
        var dialog = view.Find("dialog");
        var accept = view.Find("dialogAccept");
        var cancel = view.Find("dialogCancel");
        foreach (bool light in new[] { false, true })
        {
            model.LightTheme = light;
            foreach (var size in new[] { (840u, 620u, 1f), (840u, 620u, 1.5f), (840u, 620u, 2f), (1180u, 800u, 1f) })
            {
                host.Resize((uint)(size.Item1 * size.Item3), (uint)(size.Item2 * size.Item3), size.Item3);
                model.Home(); Render();
                var home = view.Find("hero").Bounds;
                var confirmation = model.Confirm("Удалить сборку?", "Выживание\nМиры, моды и скриншоты будут удалены безвозвратно.", "Удалить", destructive: true);
                Render();
                Check(Inside(dialog, view.Find("modalLayer")) && Math.Abs(dialog.Bounds.X + dialog.Bounds.Width / 2 - size.Item1 / 2f) < 1, "modal is centered and contained at every supported size and scale");
                Check(view.Find("hero").Bounds == home && Visible(view.Find("home")), "opening a modal preserves the underlying page layout");
                Check(!view.Find("body").Get(Ui.Enabled) && view.Find("modalLayer").Get(Ui.Background).A > 0, "modal dims and blocks the background");
                Check(Inside(accept, dialog) && Inside(cancel, dialog) && accept.Bounds.Height == cancel.Bounds.Height && accept.Bounds.Y == cancel.Bounds.Y, "modal actions share one visible baseline");
                Check(accept.Get(Ui.Background) == model.Palette.Error && Contrast(accept.Get(Ui.Foreground), accept.Get(Ui.Background)) >= 4.5, "destructive action uses a readable warning color");
                Click(view.Find("navLibrary")); Render();
                Check(model.Page == "home" && model.DialogOpen, "background click cannot navigate or dismiss the dialog");
                for (int i = 0; i < 8; i++)
                {
                    host.Key(9, i >= 4 ? KeyModifiers.Shift : KeyModifiers.None); Render();
                    Check(InDialog(host.FocusedElement), "Tab and Shift+Tab stay in the modal");
                }
                Capture(host, $"modal-confirm-{(light ? "light" : "dark")}-{size.Item1}x{size.Item2}-{size.Item3 * 100:0}pct");
                Click(cancel); Render();
                Check(confirmation.IsCompletedSuccessfully && !confirmation.Result && !model.DialogOpen, "cancel dismisses confirmation without accepting it");
            }
        }
        host.Resize(840, 620); model.LightTheme = false;
        model.BeginDialog("Название профиля", "Имя будет показано в лаунчере.", "Сохранить", () =>
        {
            if (model.Field("name").Length == 0) throw new InvalidDataException("Введи имя профиля.");
            return Task.CompletedTask;
        }, new FieldModel("name", "Имя"));
        Render(); Click(accept); Render();
        Check(model.DialogOpen && model.DialogError.Length > 0 && Inside(view.Find("dialogError"), dialog) && Inside(accept, dialog), "validation error and retry action stay visible in the dialog footer");
        Capture(host, "modal-form-error");
        var input = view.Find("dialogFields").DescendantsAndSelf().First(e => e.Type.Is(UiTypes.TextField));
        Click(input); host.Text("Новое имя"); Render();
        Check(model.Field("name") == "Новое имя", "modal fields accept keyboard input");
        Click(accept); Render();
        Check(!model.DialogOpen, "corrected form can be submitted from the footer");

        model.ShowChoices("Выбери сборку", Enumerable.Range(0, 500).Select(i => new ItemModel { Id = i.ToString(), Title = "Сборка " + i, Meta = "Minecraft 1.21.1 · Fabric" }));
        Render();
        var choices = view.Find("choices");
        var listInfo = UiVirtualList.Inspect(choices);
        Check(listInfo.Realized < 20 && Inside(choices, dialog), "large modal picker keeps a bounded virtual list");
        var search = view.Find("choiceSearch");
        Click(search); host.Text("Сборка 499"); Render();
        Check(model.DialogChoices.Count == 1 && model.DialogChoices[0].Id == "499", "modal search filters the picker");
        Capture(host, "modal-picker");
        host.Key(65, KeyModifiers.Control); host.Text("нет такой сборки"); Render();
        Check(model.DialogChoices.Count == 0 && Inside(cancel, dialog) && Inside(search, dialog), "empty picker keeps search and close reachable");
        model.CloseDialog();

        var busy = new TaskCompletionSource();
        model.BeginDialog("Сохранение", "Сохраняю профиль.", "Сохранить", () => busy.Task);
        Render(); Click(accept); Render();
        Check(model.DialogBusy && !accept.Get(Ui.Enabled) && !cancel.Get(Ui.Enabled), "pending dialog action prevents duplicate submission");
        model.CloseDialog(); Render();
        Check(model.DialogOpen, "busy dialog cannot be dismissed while its action is running");
        busy.SetResult(); Render();
        Check(!model.DialogOpen && !model.DialogBusy, "completed action releases the modal");
    }
}
