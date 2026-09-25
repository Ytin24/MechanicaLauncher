using System;
using System.IO;
using System.Linq;
using MechanicaLauncher.Core.Instances;
using MechanicaLauncher.Core.Localization;
using MechanicaLauncher.Core.Mods;
using MechanicaLauncher.Core.Profiles;
using MechanicaLauncher.Desktop;
using MechanicaLauncher.Desktop.Views;
using Nitidus;
using Nitidus.Native;

internal static partial class Program
{
    private static void CoverChecks(Pump context, string? coverImagePath = null)
    {
        string? data = Environment.GetEnvironmentVariable("MECHANICA_DATA_DIR");
        string language = Locale.CurrentLanguage;
        Environment.SetEnvironmentVariable("MECHANICA_DATA_DIR", Path.Combine(output, "cover-data"));
        try
        {
            var instances = new InstanceManager();
            using var http = new System.Net.Http.HttpClient(new CatalogHandler()) { BaseAddress = new("http://localhost") };
            using var model = new LauncherModel(new LauncherSettings { Username = "Cover test", Language = "ru", DiscordRpc = false, Animations = false }, instances, new ModrinthClient(http));
            using var presentation = new LauncherView(model);
            using var host = new HeadlessHost(presentation.View, 1180, 800);
            var view = presentation.View;
            double time = 0;
            void Render() { context.Drain(); host.Render(time += 1); host.Render(time += 1); }
            void Click(string name)
            {
                var bounds = UiTransform.VisualBounds(view.Find(name));
                host.Click(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2); Render();
            }
            void Fits(string name)
            {
                var node = view.Find(name); var b = node.Bounds;
                Check(Visible(node) && b.Width > 0 && b.Height > 0 && b.X >= 0 && b.Y >= 0 &&
                    b.X + b.Width <= host.Width + 1 && b.Y + b.Height <= host.Height + 1, "home control fits the viewport: " + name);
            }
            void ActionsFit()
            {
                foreach (string name in new[] { "instancePicker", "homeCreate", "play", "homeSettings", "homeFolder", "homeLog" }) Fits(name);
                var actions = view.Find("launchActions").DescendantsAndSelf()
                    .Where(n => n.Type.Is(UiTypes.Button) && Visible(n)).ToArray();
                Check(actions.Length == 4, "launch panel has one play action and three supporting commands");
                for (int i = 0; i < actions.Length; i++)
                    for (int j = i + 1; j < actions.Length; j++)
                    {
                        var a = actions[i].Bounds; var b = actions[j].Bounds;
                        Check(a.X + a.Width <= b.X + 1 || b.X + b.Width <= a.X + 1 ||
                            a.Y + a.Height <= b.Y + 1 || b.Y + b.Height <= a.Y + 1, "home actions do not overlap");
                    }
            }
            void NavigationFits()
            {
                var buttons = view.Find("sidebar").Children.Where(n => n.Type.Is(NavButton.Type) && Visible(n)).ToArray();
                Check(buttons.All(n => Math.Abs(n.Bounds.Height - buttons[0].Bounds.Height) < 1), "selected and unselected navigation buttons share the same height");
                foreach (var button in buttons) Fits(button.Name);
            }
            void SaveImage(string path, int width, int height, bool white = false)
            {
                using var bitmap = new System.Drawing.Bitmap(width, height);
                using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
                {
                    graphics.Clear(white ? System.Drawing.Color.White : System.Drawing.Color.SteelBlue);
                    if (!white)
                    {
                        graphics.FillRectangle(System.Drawing.Brushes.ForestGreen, 0, height / 2, width, height / 2);
                        graphics.FillRectangle(System.Drawing.Brushes.Goldenrod, width / 3, height / 3, width / 5, height / 5);
                    }
                }
                bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }

            foreach (bool light in new[] { false, true })
            {
                model.LightTheme = light;
                foreach (var size in new[] { (1180u, 800u), (840u, 620u) })
                {
                    host.Resize(size.Item1, size.Item2); Render();
                    NavigationFits();
                    Check(Visible(view.Find("homeEmpty")) && !Visible(view.Find("homeSelected")) &&
                        !Visible(view.Find("header")) && !Visible(view.Find("pageScroll")), "empty home is a fixed first-run composition");
                    foreach (string name in new[] { "createFirst", "importFirst", "browseModpacks" })
                    {
                        Fits(name);
                        var action = view.Find(name);
                        Check(action.Get(Ui.Text).Length > 0 && action.Get(Ui.Enabled), "empty home action has a readable enabled label: " + name);
                    }
                    Check(!Visible(view.Find("play")) && !Visible(view.Find("instancePicker")), "first run offers no play or empty instance selector");
                    Capture(host, $"cover-empty-{(light ? "light" : "dark")}-{size.Item1}x{size.Item2}");
                }
            }
            Click("browseModpacks");
            Check(model.Page == "catalog" && model.ContentType == "modpack", "first-run discovery opens modpacks directly");
            model.Home(); Render();

            var covered = instances.CreateInstance("Выживание", "1.21.1", LoaderType.Fabric, "0.16.10");
            var plain = instances.CreateInstance("Техномир", "1.20.1", LoaderType.Forge, "47.4.0");
            string directory = instances.GetInstanceDir(covered.Id);
            string landscape = Path.Combine(directory, "landscape.png");
            if (coverImagePath == null) SaveImage(landscape, 1600, 900);
            else File.Copy(Path.GetFullPath(coverImagePath), landscape);
            SaveImage(Path.Combine(directory, "wide.png"), 1400, 240);
            SaveImage(Path.Combine(directory, "portrait.png"), 240, 960);
            SaveImage(Path.Combine(directory, "bright.png"), 800, 800, true);
            File.WriteAllText(Path.Combine(directory, "broken.png"), "not an image");

            void SetCover(string? filename)
            {
                covered.CoverPath = filename; instances.SaveInstance(covered); model.SetInstance(covered.Id); Render();
            }
            void CropFits()
            {
                var cover = view.Find("heroCover"); var bounds = cover.Bounds;
                var probe = new CoverImageProbe(view.TextService!);
                cover.DrawContent!(cover, probe, view.Root.Bounds);
                var source = model.HeroImage.FrameAt(0, out _)!.Source;
                Check(probe.Count == 1 && ReferenceEquals(probe.Source, model.HeroImage), "cover draws the selected instance image once");
                var drawn = probe.Destination;
                Check(Math.Abs(drawn.Width / drawn.Height - source.Width / source.Height) < .002f, "cover preserves the source aspect ratio");
                Check(drawn.X <= bounds.X + 1 && drawn.Y <= bounds.Y + 1 && drawn.X + drawn.Width >= bounds.X + bounds.Width - 1 &&
                    drawn.Y + drawn.Height >= bounds.Y + bounds.Height - 1, "cover fills both viewport dimensions without letterboxing");
                Check(Math.Abs(drawn.X + drawn.Width / 2 - bounds.X - bounds.Width / 2) <= 1 &&
                    Math.Abs(drawn.Y + drawn.Height / 2 - bounds.Y - bounds.Height / 2) <= 1, "cover crop stays centered");
                Check(probe.Clip is { } clip && clip.X >= bounds.X - 1 && clip.Y >= bounds.Y - 1 &&
                    clip.X + clip.Width <= bounds.X + bounds.Width + 1 && clip.Y + clip.Height <= bounds.Y + bounds.Height + 1,
                    "cover crop cannot paint over controls outside its region");
            }
            foreach (bool light in new[] { false, true })
            {
                model.LightTheme = light;
                foreach (var size in new[] { (1180u, 800u), (840u, 620u) })
                {
                    host.Resize(size.Item1, size.Item2); SetCover("landscape.png");
                    NavigationFits();
                    Check(model.HasHeroImage && Visible(view.Find("heroCover")) && Visible(view.Find("coverShade")) && !Visible(view.Find("worldBlock")),
                        "selected cover replaces the small instance icon");
                    Check(!Visible(view.Find("header")) && !Visible(view.Find("pageScroll")), "cover home keeps launch controls outside page scrolling");
                    float coveredHeight = view.Find("hero").Bounds.Height;
                    ActionsFit(); CropFits();
                    Capture(host, $"cover-image-{(light ? "light" : "dark")}-{size.Item1}x{size.Item2}");

                    model.SetInstance(plain.Id); Render(); ActionsFit();
                    Check(!model.HasHeroImage && model.HeroImage == ImageSource.Empty && !Visible(view.Find("heroCover")) && !Visible(view.Find("coverShade")) && Visible(view.Find("worldBlock")),
                        "switching to an instance without a cover removes the previous image and shade");
                    Check(view.Find("hero").Bounds.Height < coveredHeight && view.Find("launchActions").Bounds.Y < size.Item2 - 100,
                        "an instance without a cover uses compact natural content height");
                    Capture(host, $"cover-none-{(light ? "light" : "dark")}-{size.Item1}x{size.Item2}");
                }
                host.Resize(840, 620); SetCover("bright.png");
                var title = view.Find("heroTitle"); var meta = view.Find("heroMeta");
                title.Set(Ui.Opacity, 0f); meta.Set(Ui.Opacity, 0f); Render();
                byte[] pixels = host.ReadPixels();
                title.Set(Ui.Opacity, 1f); meta.Set(Ui.Opacity, 1f); Render();
                foreach (var label in new[] { title, meta })
                {
                    double minimum = double.MaxValue;
                    var bounds = label.Bounds;
                    for (int y = 0; y < 5; y++)
                        for (int x = 0; x < 9; x++)
                        {
                            int px = (int)(bounds.X + 2 + Math.Max(0, bounds.Width - 4) * x / 8);
                            int py = (int)(bounds.Y + 2 + Math.Max(0, bounds.Height - 4) * y / 4);
                            int index = (py * (int)host.Width + px) * 4;
                            var background = Color.Hex((uint)(pixels[index] << 16 | pixels[index + 1] << 8 | pixels[index + 2]));
                            minimum = Math.Min(minimum, Contrast(label.Get(Ui.Foreground), background));
                        }
                    Check(minimum >= (label == title ? 3 : 4.5), $"{label.Name} remains readable over a white cover in {(light ? "light" : "dark")} theme: {minimum:F2}");
                }
                Capture(host, $"cover-bright-{(light ? "light" : "dark")}-840x620");
            }

            foreach (string filename in new[] { "wide.png", "portrait.png" }) { SetCover(filename); CropFits(); }
            foreach (string? filename in new[] { "broken.png", "missing.png", null })
            {
                SetCover("landscape.png"); SetCover(filename);
                Check(!model.HasHeroImage && model.HeroImage == ImageSource.Empty &&
                    !Visible(view.Find("heroCover")) && !Visible(view.Find("coverShade")), "invalid or removed cover leaves no stale image: " + filename);
                ActionsFit();
            }
            covered.Name = "Выживание с друзьями — строительство большого города, исследование миров и приключения";
            SetCover("landscape.png");
            foreach (bool light in new[] { false, true })
            {
                model.LightTheme = light; Render(); ActionsFit(); Fits("heroTitle"); Fits("heroMeta");
                Check(view.Find("heroTitle").Get(Ui.Text) == covered.Name, "home title uses the actual long instance name");
                Check(view.Find("heroMeta").Bounds.Y + view.Find("heroMeta").Bounds.Height <= view.Find("homeLaunchPanel").Bounds.Y + 1,
                    "long instance name does not overlap the launch panel");
                Capture(host, $"cover-long-name-{(light ? "light" : "dark")}-840x620");
            }
            Click("homeLog"); Fits("gameLog"); ActionsFit();
            Check(model.ShowLog, "launch panel opens the actual game log");
            Capture(host, "cover-log-840x620");
            Click("homeLog"); Check(!model.ShowLog, "launch panel closes the game log");

            SetCover(null);
            var statusProperty = typeof(GameSessions).GetProperty(nameof(GameSessions.Status))!;
            string status = string.Join('\n', Enumerable.Repeat("Не удалось загрузить файл сборки: libraries/net/minecraft/client/1.21.1/client-1.21.1.jar", 6));
            statusProperty.SetValue(model.Sessions, status); model.Changed(); Render();
            var heroScroll = view.Find("heroTextScroll");
            foreach (bool light in new[] { false, true })
            {
                model.LightTheme = light; UiScroll.ToTop(heroScroll); Render();
                Click("homeLog"); UiScroll.ToTop(heroScroll); Render();
                Check(!model.HasHeroImage && model.ShowLog && Visible(view.Find("homeSessionStatus")), "overflow fixture combines no cover, a long name, session status and the log");
                ActionsFit(); Fits("gameLog");
                var actionsBounds = view.Find("launchActions").Bounds;
                var logBounds = view.Find("gameLog").Bounds;
                var scrollInfo = UiScroll.Inspect(heroScroll);
                Check(scrollInfo.VerticalBarVisible && scrollInfo.Viewport.Height > 0 && scrollInfo.Viewport.Y + scrollInfo.Viewport.Height <= actionsBounds.Y + 1,
                    "long instance details have a bounded scroll viewport above the launch panel");
                var title = view.Find("heroTitle");
                UiScroll.ScrollTo(heroScroll, 0, title.Bounds.Y - scrollInfo.Viewport.Y); Render();
                var viewport = UiScroll.Inspect(heroScroll).Viewport;
                Check(title.Bounds.Y >= viewport.Y - 1 && title.Bounds.Y + title.Bounds.Height <= viewport.Y + viewport.Height + 1 && title.Get(Ui.Text) == covered.Name,
                    "the full long instance name can be brought into view above the open log");
                Capture(host, $"cover-none-long-name-log-{(light ? "light" : "dark")}-840x620");
                host.Send(new(InputType.Move, viewport.X + viewport.Width / 2, viewport.Y + viewport.Height / 2, 0));
                for (int i = 0; i < 30; i++) host.Send(new(InputType.Wheel, 0, 0, -120));
                Render();
                var text = view.Find("homeSessionStatus");
                float bottom = text.Bounds.Y + text.Bounds.Height;
                Check(UiScroll.Inspect(heroScroll).VerticalOffset > 0 && bottom <= viewport.Y + viewport.Height + 1 && bottom > viewport.Y,
                    "mouse scrolling reaches the final session-status line");
                Check(view.Find("launchActions").Bounds == actionsBounds && view.Find("gameLog").Bounds == logBounds,
                    "scrolling long home details leaves launch actions and the log fixed");
                foreach (string name in new[] { "play", "homeSettings", "homeFolder", "homeLog", "gameLog" }) Fits(name);
                Capture(host, $"cover-none-long-status-log-{(light ? "light" : "dark")}-840x620");
                statusProperty.SetValue(model.Sessions, status + "\nПовторная попытка загрузки."); model.Changed(); Render();
                Check(UiScroll.Inspect(heroScroll).VerticalOffset > 0, "a session-status update does not return the reader to the top");
                Click("homeLog"); Check(!model.ShowLog, "the fixed log action remains usable after scrolling long details");
            }
            statusProperty.SetValue(model.Sessions, ""); model.Changed(); UiScroll.ToTop(heroScroll); Render();
            Click("instancePicker");
            Check(model.DialogOpen && model.DialogChoices.Count == 2, "cover toolbar opens the instance selector");
            model.DialogChoices.Single(c => c.Id == plain.Id).Invoke(); Render();
            Check(!model.DialogOpen && model.SelectedInstance?.Id == plain.Id && !model.HasHeroImage, "instance selection updates the cover composition immediately");
            Click("homeSettings");
            Check(model.Page == "instance" && model.EditingInstance?.Id == plain.Id, "launch settings target the selected instance");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MECHANICA_DATA_DIR", data);
            Locale.Init(language);
        }
    }

    private sealed class CoverImageProbe(ITextService textService) : ICanvas
    {
        public ITextService TextService { get; } = textService;
        public int Count { get; private set; }
        public ImageSource? Source { get; private set; }
        public Rect Destination { get; private set; }
        public Rect? Clip { get; private set; }
        public void Rectangle(Rect bounds, Color color, float radius = 0, Rect? clip = null) { }
        public void StrokeRectangle(Rect bounds, Color color, float thickness = 1, float radius = 0, Rect? clip = null) { }
        public void Gradient(Rect bounds, Color top, Color bottom, float radius = 0, Rect? clip = null) { }
        public void Shapes(ReadOnlySpan<Shape> shapes) { }
        public void Text(string text, float x, float y, float size, Color color, Rect? clip = null,
            string fontFamily = "Segoe UI", Alignment alignment = Alignment.Left, float maxWidth = 4096, bool wrap = false) { }
        public void Text(string text, float x, float y, TextFormat format, Color color, Rect? clip = null) { }
        public void Image(ImageSource source, Rect bounds, Rect? clip = null)
        { Count++; Source = source; Destination = bounds; Clip = clip; }
    }
}
