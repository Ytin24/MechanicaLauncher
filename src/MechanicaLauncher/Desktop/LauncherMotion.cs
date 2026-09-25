using Nitidus.Motion;
using MechanicaLauncher.Desktop.Views;

namespace MechanicaLauncher.Desktop;

internal sealed class LauncherMotion(CompiledView view, LauncherModel model) : IDisposable
{
    private static readonly Easing Arrive = Ease.CubicBezier(.22, 1, .36, 1);
    private static readonly Easing Travel = Ease.CubicBezier(.2, 0, .2, 1);
    private const double EnterSeconds = .34, ChangeSeconds = .2, FeedbackSeconds = .12;
    private sealed class State(long generation, ImageSource picture)
    {
        public long Generation { get; } = generation;
        public List<IAnimationDriver> Transitions { get; } = [];
        public Animation? Reveal;
        public ImageSource Picture = picture;
    }

    private readonly Dictionary<Element, State> states = [];
    private Timeline? pageMotion, dialogMotion, pressMotion, pageExitMotion, dialogExitMotion;
    private LauncherFrame? pageFrame, dialogFrame;
    private Tween<float>? navigationMotion;
    private Element? selectedNavigation;
    private Rect sidebarBounds, viewport;
    private LauncherPalette? palette;
    private Color dialogBackdrop;
    private string? route, previousPage, notice;
    private int dialogGeneration;
    private bool reduced, synchronizing, disposed, dialogOpen, playPressed;
    private AnimationClock Clock => view.Animations;

    internal void Synchronize()
    {
        if (disposed || synchronizing) return;
        synchronizing = true;
        try
        {
            bool preferenceChanged = reduced != Clock.ReducedMotion;
            if (preferenceChanged) { Reset(); reduced = Clock.ReducedMotion; }
            if (viewport != view.Root.Bounds || !ReferenceEquals(palette, model.Palette))
            {
                ClearExits(); viewport = view.Root.Bounds; palette = model.Palette;
            }
            string nextRoute = model.Page + "/" + (model.Page == "instance" ? model.InstanceTab : model.Page == "project" ? model.ProjectTab : "");
            bool routeChanged = route != nextRoute;
            route = nextRoute;

            var visible = view.Root.DescendantsAndSelf().Where(IsVisible).ToArray();
            var attached = visible.ToHashSet();
            foreach (var (node, state) in states.ToArray())
                if (!attached.Contains(node) || node.Generation != state.Generation)
                {
                    Release(state); states.Remove(node);
                }

            foreach (var node in visible)
            {
                bool image = node.Type.Is(FitImage.Type);
                var picture = image ? node.Get(FitImage.Properties.Picture) : ImageSource.Empty;
                if (!states.TryGetValue(node, out var state))
                {
                    state = new(node.Generation, picture); states.Add(node, state);
                    if (!reduced)
                    {
                        AddTransitions(node, state);
                        if (!routeChanged && !preferenceChanged && node.Name is "advanced" or "gameLog")
                            state.Reveal = Enter(node, "reveal:" + node.Name, 0, 0, 12, .26);
                        if (!routeChanged && !preferenceChanged && image && picture != ImageSource.Empty && node.Bounds.Height >= 150)
                            state.Reveal = Fade(node, .2f, EnterSeconds, "image");
                    }
                }
                if (image && !ReferenceEquals(state.Picture, picture))
                {
                    state.Picture = picture; state.Reveal?.Kill(); state.Reveal = null;
                    if (!reduced && picture != ImageSource.Empty && node.Bounds.Height >= 150)
                        state.Reveal = Fade(node, .2f, EnterSeconds, "image");
                }
            }

            SynchronizeNavigation(preferenceChanged);
            SynchronizeDialog(preferenceChanged);
            SynchronizePress();
            if (notice != model.Message)
            {
                notice = model.Message;
                var node = view.Find("notice");
                if (states.TryGetValue(node, out var state))
                {
                    state.Reveal?.Kill(); state.Reveal = null;
                    if (!reduced && !preferenceChanged && notice.Length > 0)
                        state.Reveal = Enter(node, "notice", 0, 0, -10, .26);
                }
            }
            if (routeChanged || model.DialogOpen || model.TLauncherBlocked)
            {
                pageMotion?.Kill(); pageMotion = null;
                if (model.DialogOpen || model.TLauncherBlocked) ClearPageExit();
                if (routeChanged && !reduced && !preferenceChanged && !model.DialogOpen && !model.TLauncherBlocked)
                    EnterPage(previousPage == model.Page);
            }
            previousPage = model.Page;
        }
        finally { synchronizing = false; }
    }

    private void AddTransitions(Element node, State state)
    {
        if (node.Type.Is(UiTypes.Button) || node.Type.Is(UiTypes.TextField))
        {
            state.Transitions.Add(Clock.Transition(node, Ui.Background, FeedbackSeconds, Ease.QuadOut));
            state.Transitions.Add(Clock.Transition(node, Ui.BorderColor, FeedbackSeconds, Ease.QuadOut));
        }
        if (node.Classes.Contains("entry")) state.Transitions.Add(Clock.Transition(node, Ui.BorderColor, ChangeSeconds, Ease.QuadOut));
        if (node.Type.Is(UiTypes.ProgressBar)) state.Transitions.Add(Clock.Transition(node, Ui.Progress, ChangeSeconds, Ease.QuadOut));
    }

    private void SynchronizeNavigation(bool preferenceChanged)
    {
        var sidebar = view.Find("sidebar");
        var selected = sidebar.Children.FirstOrDefault(node => node.Type.Is(NavButton.Type) && node.Get(Ui.Selected) && IsVisible(node));
        sidebar.Set(Sidebar.Properties.IndicatorVisible, selected != null);
        if (selected == null) { navigationMotion?.Kill(); navigationMotion = null; selectedNavigation = null; return; }
        float target = selected.Bounds.Y - sidebar.Bounds.Y;
        sidebar.Set(Sidebar.Properties.IndicatorHeight, selected.Bounds.Height);
        bool moved = sidebarBounds != sidebar.Bounds;
        if (selected == selectedNavigation && !moved && !preferenceChanged && sidebar.GetBaseValue(Sidebar.Properties.IndicatorY) == target) return;
        float from = sidebar.Get(Sidebar.Properties.IndicatorY);
        navigationMotion?.Kill(); navigationMotion = null;
        sidebar.Set(Sidebar.Properties.IndicatorY, target);
        if (!reduced && !preferenceChanged && !moved && selectedNavigation != null && selected != selectedNavigation)
        {
            navigationMotion = Clock.FromTo(sidebar, Sidebar.Properties.IndicatorY, from, target, .32, new() { Name = "navigation", Ease = Travel });
            navigationMotion.Completed += animation => animation.Kill();
            navigationMotion.Seek(0);
        }
        selectedNavigation = selected; sidebarBounds = sidebar.Bounds;
    }

    private void SynchronizeDialog(bool preferenceChanged)
    {
        if (!model.DialogOpen)
        {
            dialogMotion?.Kill(); dialogMotion = null;
            if (dialogOpen && dialogFrame != null && !reduced && !preferenceChanged)
            {
                var exit = view.Find("dialogExit");
                exit.Set(Ui.Visible, true);
                exit.Set(Ui.Background, dialogBackdrop);
                dialogExitMotion = Clock.Timeline(new() { Name = "dialog-exit" });
                dialogExitMotion.FromTo(exit, Ui.Opacity, 1f, 0f, .18, 0, new() { Ease = Ease.QuadIn });
                var ghost = view.Find("dialogGhost");
                dialogExitMotion.FromTo(ghost, Ui.TranslateY, 0f, 16f, .18, 0, new() { Ease = Ease.QuadIn });
                dialogExitMotion.FromTo(ghost, Ui.ScaleX, 1f, .98f, .18, 0, new() { Ease = Ease.QuadIn });
                dialogExitMotion.FromTo(ghost, Ui.ScaleY, 1f, .98f, .18, 0, new() { Ease = Ease.QuadIn });
                dialogExitMotion.Completed += _ => ClearDialogExit();
                dialogExitMotion.Seek(0);
            }
            dialogOpen = false; return;
        }
        ClearDialogExit();
        if (dialogOpen && dialogGeneration == model.DialogGeneration) return;
        dialogMotion?.Kill(); dialogMotion = null;
        if (!reduced && !preferenceChanged)
        {
            if (dialogOpen) dialogMotion = Enter(view.Find("dialogContent"), "dialog-content", .35f, 0, 8, .24);
            else
            {
                var dialog = view.Find("dialog");
                dialogMotion = Enter(dialog, "dialog", 0, 0, 24, EnterSeconds);
                dialogMotion.FromTo(dialog, Ui.ScaleX, .96f, 1f, EnterSeconds, 0, new() { Ease = Arrive });
                dialogMotion.FromTo(dialog, Ui.ScaleY, .96f, 1f, EnterSeconds, 0, new() { Ease = Arrive });
                var layer = view.Find("modalLayer");
                dialogMotion.FromTo(layer, Ui.Background, new Color(0, 0, 0, 0), layer.Get(Ui.Background), ChangeSeconds, 0, new() { Ease = Ease.QuadOut });
                dialogMotion.Seek(0);
            }
        }
        dialogOpen = true; dialogGeneration = model.DialogGeneration;
    }

    internal void CapturePage()
    {
        if (disposed || Clock.ReducedMotion || previousPage != model.Page || view.TextService == null || pageExitMotion is { IsActive: true }) return;
        var next = Capture(view.Find("workspace"));
        ClearPageExit(); pageFrame = next;
        view.Find("pageExit").Set(Ui.Visible, next != null);
    }

    internal void CaptureDialog()
    {
        if (disposed || Clock.ReducedMotion || !dialogOpen || view.TextService == null) return;
        var next = Capture(view.Find("dialog"));
        dialogBackdrop = view.Find("modalLayer").Get(Ui.Background);
        ClearDialogExit(); dialogFrame = next;
        view.Find("dialogExit").Set(Ui.Visible, next != null);
    }

    private LauncherFrame? Capture(Element element)
    {
        if (view.TextService == null || element.Bounds.Width <= 0 || element.Bounds.Height <= 0) return null;
        var frame = new LauncherFrame(view.TextService);
        UiPaint.Draw(frame, element, view.Root.Bounds);
        return frame;
    }

    internal void DrawPageExit(ICanvas canvas) => pageFrame?.Draw(canvas);
    internal void DrawDialogExit(ICanvas canvas) => dialogFrame?.Draw(canvas);

    private void EnterPage(bool tab)
    {
        var page = model.Page == "home" ? view.Find("home") : view.Find("pages").Children.FirstOrDefault(IsVisible);
        if (page == null) return;
        pageMotion = Clock.Timeline(new() { Name = "page" });
        float direction = IsDetail(model.Page) ? 1 : IsDetail(previousPage) ? -1 : 0;
        if (pageFrame != null && !tab && pageExitMotion is not { IsActive: true })
        {
            var exit = view.Find("pageExit");
            exit.Set(Ui.Visible, true);
            pageExitMotion = Clock.Timeline(new() { Name = "page-exit" });
            pageExitMotion.FromTo(exit, Ui.Opacity, 1f, 0f, .09, 0, new() { Ease = Ease.QuadIn });
            pageExitMotion.FromTo(exit, direction == 0 ? Ui.TranslateY : Ui.TranslateX, 0f, direction == 0 ? -12f : -direction * 22, .12, 0, new() { Ease = Ease.QuadIn });
            pageExitMotion.Completed += _ => ClearPageExit();
            pageExitMotion.Seek(0);
        }
        if (tab)
        {
            var content = model.Page == "project" ? view.Find("article") : page.Children.LastOrDefault(IsVisible);
            if (content != null) AddEntrance(pageMotion, content, .2f, 0, 14, .24, 0);
        }
        else
        {
            double delay = pageFrame != null ? .095 : 0;
            if (model.Page == "home")
            {
                if (model.SelectedInstance == null) AddEntrance(pageMotion, view.Find("homeEmpty"), 0, 0, 14, EnterSeconds, delay);
                else
                {
                    AddEntrance(pageMotion, view.Find("homeToolbar"), 0, 0, -8, .28, delay);
                    if (model.HasHeroImage) AddEntrance(pageMotion, view.Find("heroCover"), .35f, 0, 0, EnterSeconds, delay);
                    AddEntrance(pageMotion, view.Find("homeDetails"), 0, 0, 14, EnterSeconds, delay + .035);
                    AddEntrance(pageMotion, view.Find("launchActions"), 0, 0, 12, .28, delay + .07);
                }
            }
            else
            {
                AddEntrance(pageMotion, view.Find("header"), 0, direction * 12, direction == 0 ? -8 : 0, .28, delay);
                int group = 0;
                foreach (var node in page.Children.Where(IsVisible))
                    AddEntrance(pageMotion, node, 0, direction * 36, direction == 0 ? 24 : 0, EnterSeconds, delay + .035 + Math.Min(group++, 3) * .045);
            }
        }
        pageMotion.Completed += animation => animation.Kill();
        pageMotion.Seek(0);
    }

    private void SynchronizePress()
    {
        var play = view.Find(model.SelectedInstance == null ? "createFirst" : "play");
        bool pressed = IsVisible(play) && play.Get(Ui.Enabled) && play.HasState("pressed");
        if (pressed == playPressed) return;
        playPressed = pressed;
        float fromX = play.Get(Ui.ScaleX), fromY = play.Get(Ui.ScaleY);
        pressMotion?.Kill(); pressMotion = null;
        if (reduced || !IsVisible(play)) return;
        float to = pressed ? .97f : 1;
        pressMotion = Clock.Timeline(new() { Name = "play-press" });
        pressMotion.FromTo(play, Ui.ScaleX, fromX, to, pressed ? .09 : .22, 0, new() { Ease = Arrive });
        pressMotion.FromTo(play, Ui.ScaleY, fromY, to, pressed ? .09 : .22, 0, new() { Ease = Arrive });
        if (!pressed) pressMotion.Completed += animation => animation.Kill();
        pressMotion.Seek(0);
    }

    private Timeline Enter(Element node, string name, float opacity, float x, float y, double seconds)
    {
        var animation = Clock.Timeline(new() { Name = name });
        AddEntrance(animation, node, opacity, x, y, seconds, 0);
        animation.Completed += completed => completed.Kill();
        animation.Seek(0); return animation;
    }

    private static void AddEntrance(Timeline animation, Element node, float opacity, float x, float y, double seconds, double at)
    {
        animation.FromTo(node, Ui.Opacity, opacity, 1f, Math.Min(seconds, ChangeSeconds), at, new() { Ease = Ease.QuadOut });
        if (x != 0) animation.FromTo(node, Ui.TranslateX, x, 0, seconds, at, new() { Ease = Arrive });
        if (y != 0) animation.FromTo(node, Ui.TranslateY, y, 0, seconds, at, new() { Ease = Arrive });
    }

    private void ClearPageExit()
    {
        pageExitMotion?.Kill(); pageExitMotion = null; pageFrame = null;
        view.Find("pageExit").Set(Ui.Visible, false);
    }
    private void ClearDialogExit()
    {
        dialogExitMotion?.Kill(); dialogExitMotion = null; dialogFrame = null;
        view.Find("dialogExit").Set(Ui.Visible, false);
    }
    private void ClearExits() { ClearPageExit(); ClearDialogExit(); }

    private static bool IsDetail(string? page) => page is "instance" or "project" or "create" or "skin";

    private Tween<float> Fade(Element node, float from, double seconds, string name)
    {
        var fade = Clock.FromTo(node, Ui.Opacity, from, 1f, seconds, new() { Name = name, Ease = Ease.QuadOut });
        fade.Completed += animation => animation.Kill();
        fade.Seek(0); return fade;
    }

    private static bool IsVisible(Element node)
    {
        for (Element? parent = node; parent != null; parent = parent.Parent)
            if (!parent.Get(Ui.Visible)) return false;
        return true;
    }

    private void Release(State state)
    {
        if (Clock.IsDisposed) return;
        state.Reveal?.Kill();
        foreach (var transition in state.Transitions) Clock.Remove(transition);
    }
    private void Reset()
    {
        foreach (var state in states.Values) Release(state);
        states.Clear();
        if (!Clock.IsDisposed)
        {
            pageMotion?.Kill(); dialogMotion?.Kill(); navigationMotion?.Kill(); pressMotion?.Kill();
            ClearExits();
        }
        pageMotion = dialogMotion = pressMotion = null; navigationMotion = null;
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; Reset();
    }
}
