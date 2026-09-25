using Nitidus.Motion;
using MechanicaLauncher.Desktop.Views;

namespace MechanicaLauncher.Desktop;

public sealed class LauncherView : IDisposable
{
    public Shell View { get; }
    private readonly LauncherModel model;
    private LauncherPalette? palette;
    private readonly List<IDisposable> transitions = [];
    private readonly LauncherMotion motion;
    private bool configuringImages;
    private string? scrollRoute;
    private int scrollDialogGeneration;
    private string? heroInstance;
    private ImageSource? heroImage;
    public LauncherView(LauncherModel model)
    {
        this.model = model;
        View = new(model);
        ConfigureImages();
        View.Styles.Invalidated += ConfigureImages;
        ApplyTheme();
        motion = new(View, model);
        model.Navigating += motion.CapturePage;
        model.DialogClosing += motion.CaptureDialog;
        View.Root.DrawContent = (_, _, _) => { ConfigureImages(); motion.Synchronize(); };
        View.Find("pageExit").DrawContent = (_, canvas, _) => motion.DrawPageExit(canvas);
        View.Find("dialogGhost").DrawContent = (_, canvas, _) => motion.DrawDialogExit(canvas);
        View.Find("sidebar").DrawContent = (node, canvas, clip) =>
        {
            if (node.Get(Sidebar.Properties.IndicatorVisible))
            {
                float padding = node.Get(Ui.Padding), y = node.Bounds.Y + node.Get(Sidebar.Properties.IndicatorY);
                canvas.Rectangle(new(node.Bounds.X + padding, y, node.Bounds.Width - padding * 2, node.Get(Sidebar.Properties.IndicatorHeight)), model.Palette.Hero, 6, clip);
            }
        };
        View.Find("coverShade").DrawContent = (node, canvas, clip) =>
        {
            var bounds = node.Bounds;
            var details = View.Find("homeDetails").Bounds;
            float start = Math.Max(bounds.Y, details.Y - 70);
            float solid = Math.Max(start, details.Y - 12);
            canvas.Gradient(new(bounds.X, bounds.Y, bounds.Width, Math.Min(130, bounds.Height)), new(0, 0, 0, .28f), new(0, 0, 0, 0), 0, clip);
            canvas.Gradient(new(bounds.X, start, bounds.Width, solid - start), new(0, 0, 0, 0), new(0, 0, 0, .9f), 0, clip);
            canvas.Rectangle(new(bounds.X, solid, bounds.Width, Math.Max(0, bounds.Y + bounds.Height - solid)), new(0, 0, 0, .9f), 0, clip);
        };
        View.Find("worldBlock").DrawContent = LauncherArtwork.Block;
        foreach (var nav in View.Root.DescendantsAndSelf().Where(e => e.Type.Is(NavButton.Type)))
            nav.DrawContent = (element, canvas, clip) =>
            {
                var bounds = element.Bounds;
                var color = element.Get(Ui.Foreground);
                var hint = element.Get(NavButton.Properties.Hint);
                if (element.Get(NavButton.Properties.Stacked))
                {
                    canvas.Text(element.Get(NavButton.Properties.Glyph), bounds.X, bounds.Y + (bounds.Height - 40) / 2,
                        19, color, clip, fontFamily: "Segoe MDL2 Assets", alignment: Alignment.Center, maxWidth: bounds.Width);
                    canvas.Text(element.Get(NavButton.Properties.Caption), bounds.X, bounds.Y + (bounds.Height - 40) / 2 + 28,
                        11, color, clip, alignment: Alignment.Center, maxWidth: bounds.Width);
                    var badge = element.Get(NavButton.Properties.Badge);
                    if (badge.Length > 0)
                    {
                        float width = badge.Length > 2 ? 25 : 18;
                        float x = bounds.X + bounds.Width - width - 2, y = bounds.Y + 3;
                        canvas.Rectangle(new(x, y, width, 16), model.Palette.Accent, 8, clip);
                        canvas.Text(badge.Length > 2 ? "99+" : badge, x, y + 1, 10, model.Palette.OnAccent, clip, alignment: Alignment.Center, maxWidth: width);
                    }
                    return;
                }
                float top = bounds.Y + (bounds.Height - (hint.Length > 0 ? 34 : 18)) / 2;
                canvas.Text(element.Get(NavButton.Properties.Glyph), bounds.X + 12, bounds.Y + (bounds.Height - 18) / 2,
                    17, color, clip, fontFamily: "Segoe MDL2 Assets", maxWidth: 22);
                canvas.Text(element.Get(NavButton.Properties.Caption), bounds.X + 40, top,
                    13, color, clip, maxWidth: Math.Max(1, bounds.Width - 48));
                if (hint.Length > 0)
                    canvas.Text(hint, bounds.X + 40, top + 20, 11, model.Palette.Muted, clip, maxWidth: Math.Max(1, bounds.Width - 48));
            };
        var skin = View.Find("skinCanvas");
        skin.DrawContent = model.DrawSkin;
        transitions.Add(View.Animations.Observe(input => model.SkinInput(skin, input)));
        model.PropertyChanged += ModelChanged;
    }
    private void ModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        ApplyTheme();
        if (heroInstance != model.SelectedInstance?.Id || !ReferenceEquals(heroImage, model.HeroImage))
        {
            heroInstance = model.SelectedInstance?.Id; heroImage = model.HeroImage;
            UiScroll.ToTop(View.Find("heroTextScroll"));
        }
        if (model.DialogOpen && scrollDialogGeneration != model.DialogGeneration)
        {
            scrollDialogGeneration = model.DialogGeneration;
            UiScroll.ToTop(View.Find("dialogScroll"));
        }
        string route = model.Page + "/" + model.InstanceTab;
        if (route == scrollRoute) return;
        scrollRoute = route;
        UiScroll.ToTop(View.Find("pageScroll"));
        UiScroll.ToTop(View.Find("heroTextScroll"));
    }
    private void ConfigureImages()
    {
        if (configuringImages) return;
        configuringImages = true;
        try
        {
            foreach (var element in View.Root.DescendantsAndSelf().Where(e => e.Type.Is(FitImage.Type) && e.DrawContent == null))
                element.DrawContent = (node, canvas, clip) =>
                {
                    var source = node.Get(FitImage.Properties.Picture);
                    if (source.FrameAt(0, out _) is not { } frame || frame.Source.Width <= 0 || frame.Source.Height <= 0) return;
                    var bounds = node.Bounds;
                    bool cover = node.Get(FitImage.Properties.Cover);
                    float scale = cover ? Math.Max(bounds.Width / frame.Source.Width, bounds.Height / frame.Source.Height)
                        : Math.Min(bounds.Width / frame.Source.Width, bounds.Height / frame.Source.Height);
                    float width = frame.Source.Width * scale, height = frame.Source.Height * scale;
                    float left = Math.Max(bounds.X, clip.X), top = Math.Max(bounds.Y, clip.Y);
                    var imageClip = new Rect(left, top, Math.Max(0, Math.Min(bounds.X + bounds.Width, clip.X + clip.Width) - left),
                        Math.Max(0, Math.Min(bounds.Y + bounds.Height, clip.Y + clip.Height) - top));
                    canvas.Image(source, new(bounds.X + (bounds.Width - width) / 2, bounds.Y + (bounds.Height - height) / 2, width, height), imageClip);
                };
        }
        finally { configuringImages = false; }
    }
    private void ApplyTheme()
    {
        View.Animations.ReducedMotion = !model.Animations || !Platform.SystemAnimations;
        if (ReferenceEquals(palette, model.Palette)) return;
        palette = model.Palette; View.Styles.SetTheme(palette.Theme);
    }
    public void Dispose()
    {
        model.PropertyChanged -= ModelChanged;
        model.Navigating -= motion.CapturePage;
        model.DialogClosing -= motion.CaptureDialog;
        motion.Dispose();
        View.Styles.Invalidated -= ConfigureImages;
        foreach (var transition in transitions) transition.Dispose();
    }
}
