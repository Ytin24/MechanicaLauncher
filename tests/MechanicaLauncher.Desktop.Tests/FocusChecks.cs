using System;
using System.IO;
using System.Linq;
using MechanicaLauncher.Core.Instances;
using MechanicaLauncher.Core.Profiles;
using MechanicaLauncher.Desktop;
using Nitidus;
using Nitidus.Native;

internal static partial class Program
{
    private static void FocusChecks()
    {
        string? data = Environment.GetEnvironmentVariable("MECHANICA_DATA_DIR");
        Environment.SetEnvironmentVariable("MECHANICA_DATA_DIR", Path.Combine(output, "focus-data"));
        try
        {
            var instances = new InstanceManager();
            using var model = new LauncherModel(new LauncherSettings { Username = "Preview", Language = "ru", DiscordRpc = false, Animations = false }, instances);
            using var presentation = new LauncherView(model);
            using var host = new HeadlessHost(presentation.View, 1180, 800);
            var view = presentation.View;
            double time = 0;
            void Render() { host.Render(time += 1); host.Render(time += 1); }
            void Click(Element node)
            {
                var b = UiTransform.VisualBounds(node);
                host.Click(b.X + b.Width / 2, b.Y + b.Height / 2); Render();
            }
            void CheckFocusedContrast(string name)
            {
                var node = view.Find(name);
                host.Send(new(InputType.Move, -1, -1, 0)); Render();
                Check(host.FocusedElement == node && node.Get(Ui.FocusRing).A == 0, "supporting action keeps its keyboard focus local: " + name);
                var bounds = node.Bounds; byte[] pixels = host.ReadPixels();
                int x = (int)(bounds.X + bounds.Width / 2), y = (int)(bounds.Y + 5);
                int index = (y * (int)host.Width + x) * 4;
                var background = Color.Hex((uint)(pixels[index] << 16 | pixels[index + 1] << 8 | pixels[index + 2]));
                Check(Contrast(node.Get(Ui.Foreground), background) >= 4.5,
                    "focused supporting action text contrasts with its rendered fill: " + name);
                Capture(host, $"focus-{name}-{(model.LightTheme ? "light" : "dark")}");
            }
            foreach (bool light in new[] { false, true })
            {
                model.LightTheme = light; model.Home();
                UiInput.Focus(host.FocusedElement, null);
                host.Send(new(InputType.Move, -1, -1, 0)); Render();
                var home = view.Find("home");
                var viewport = home.Bounds;
                byte[] before = host.ReadPixels();
                host.Click(viewport.X + viewport.Width / 2, viewport.Y + viewport.Height - 16); Render();
                Capture(host, $"focus-home-{(light ? "light" : "dark")}");
                Check(!Visible(view.Find("pageScroll")), "home uses a fixed layout without a page-sized scroll focus target");
                Check(before.SequenceEqual(host.ReadPixels()), "clicking blank home content does not outline or tint the page");

                var play = view.Find("createFirst");
                var playBounds = play.Bounds;
                int activated = 0;
                play.Set(Ui.Command, Command.From(() => activated++));
                for (int i = 0; i < 30 && host.FocusedElement != play; i++) { host.Key(9); Render(); }
                Check(host.FocusedElement == play && play.Bounds == playBounds, "keyboard navigation reaches the primary action without shifting its layout");
                Check(play.Get(Ui.FocusRing).A == 0 && Contrast(play.Get(Ui.BorderColor), play.Get(Ui.Background)) >= 3, "primary action keeps a contrasting focus edge without an outer halo");
                Check(OutsideUnchanged(before, host.ReadPixels(), playBounds, host.Width), "keyboard focus paints only inside its button");
                host.Key(13); Render(); Check(activated == 1, "focused primary action still responds to Enter exactly once");
                Check(play.Get(Ui.Text).Length > 0 && Contrast(play.Get(Ui.Foreground), play.Get(Ui.Background)) >= 4.5, "primary action label stays readable after keyboard activation");
                Capture(host, $"focus-keyboard-{(light ? "light" : "dark")}");

                model.ShowChoices("Выбор сборки", [new() { Title = "Выживание", Action = () => activated++ }]); Render();
                var choices = view.Find("choices");
                var bounds = choices.Bounds;
                host.Click(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height - 12); Render();
                Check(host.FocusedElement == choices && choices.Get(Ui.FocusRing).A == 0, "clicking dialog list whitespace does not frame the entire list");
                var choice = choices.DescendantsAndSelf().First(n => n.Type.Is(UiTypes.Button));
                Click(choice); Check(activated == 2, "dialog choice remains clickable with local focus styling");
                Check(choice.Get(Ui.FocusRing).A == 0 && choice.Get(Ui.BorderColor) == model.Palette.Accent, "focused dialog choice has one local accent edge");
                Capture(host, $"focus-dialog-{(light ? "light" : "dark")}");
                model.CloseDialog(); Render();
                var browse = view.Find("browseModpacks");
                for (int i = 0; i < 30 && host.FocusedElement != browse; i++) { host.Key(9); Render(); }
                CheckFocusedContrast("browseModpacks");
            }
            var instance = instances.CreateInstance("Мир", "1.21.1");
            model.SetInstance(instance.Id); host.Resize(840, 620);
            foreach (bool light in new[] { false, true })
            {
                model.LightTheme = light; model.Home();
                UiInput.Focus(host.FocusedElement, null);
                host.Send(new(InputType.Move, -1, -1, 0)); Render();
                var play = view.Find("play"); var bounds = play.Bounds;
                byte[] before = host.ReadPixels();
                int activated = 0; play.Set(Ui.Command, Command.From(() => activated++));
                for (int i = 0; i < 30 && host.FocusedElement != play; i++) { host.Key(9); Render(); }
                Check(host.FocusedElement == play && play.Bounds == bounds, "keyboard reaches play on a selected instance without shifting its layout");
                Check(play.Get(Ui.FocusRing).A == 0 && Contrast(play.Get(Ui.BorderColor), play.Get(Ui.Background)) >= 3,
                    "selected instance play has a local contrasting keyboard focus edge");
                Check(OutsideUnchanged(before, host.ReadPixels(), bounds, host.Width), "selected instance keyboard focus paints only inside play");
                host.Key(13); Render(); Check(activated == 1, "selected instance play responds to Enter exactly once");
                Capture(host, $"focus-selected-{(light ? "light" : "dark")}-840x620");
                Click(view.Find("homeLog")); CheckFocusedContrast("homeLog");
                Click(view.Find("homeLog"));
                Click(view.Find("navHome")); CheckFocusedContrast("navHome");
            }
            host.Resize(1180, 800);
            model.SetInstance(instance.Id); model.LightTheme = false; model.Library(); Render();
            var list = view.Find("instanceList");
            host.Click(list.Bounds.X + 8, list.Bounds.Y + list.Bounds.Height - 16); Render();
            Check(host.FocusedElement == list && list.Get(Ui.FocusRing).A == 0, "library whitespace does not frame the entire catalog");
            Click(view.Find("librarySearch")); host.Text("Мир"); Render();
            Check(model.LibraryQuery == "Мир" && view.Find("librarySearch").Get(Ui.BorderColor) == model.Palette.Accent, "search retains text entry and its focused field edge");
            Check(view.Find("librarySearch").Get(Ui.FocusRing).A == 0, "focused search field has no stacked outer ring");
            Capture(host, "focus-library-search");
        }
        finally { Environment.SetEnvironmentVariable("MECHANICA_DATA_DIR", data); }
    }

    private static bool OutsideUnchanged(byte[] before, byte[] after, Rect bounds, uint width)
    {
        for (int pixel = 0; pixel < before.Length / 4; pixel++)
        {
            float x = pixel % width, y = pixel / width;
            if (x >= bounds.X - 1 && x < bounds.X + bounds.Width + 1 && y >= bounds.Y - 1 && y < bounds.Y + bounds.Height + 1) continue;
            for (int channel = 0; channel < 4; channel++)
                if (before[pixel * 4 + channel] != after[pixel * 4 + channel]) return false;
        }
        return true;
    }
}
