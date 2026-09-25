using System;
using System.Linq;
using System.Threading.Tasks;
using MechanicaLauncher.Desktop;
using MechanicaLauncher.Desktop.Views;
using Nitidus;
using Nitidus.Native;

internal static partial class Program
{
    private static void MotionChecks(LauncherModel model, LauncherView presentation, HeadlessHost host, Pump context, double time)
    {
        var view = presentation.View;
        bool reduced = false;
        void Frame(double seconds = 1d / 60)
        {
            context.Drain(); view.Animations.ReducedMotion = reduced; host.Render(time += seconds);
        }
        void Settle() { Frame(0); Frame(.7); Frame(.01); }
        model.Animations = true; model.Library(); Frame(0); Settle(); model.Home(); Frame(0);
        Check(view.Find("pageExit").Get(Ui.Visible) && !view.Find("pageExit").Get(Ui.Enabled), "departing page is preserved as a non-interactive visual");
        Check(view.Find("homeDetails").Get(Ui.Opacity) == 0 && view.Find("homeDetails").Get(Ui.TranslateY) is > 0 and <= 16, "home details enter with a small offset");
        Frame(.15);
        Check(view.Find("homeToolbar").Get(Ui.Opacity) >= view.Find("homeDetails").Get(Ui.Opacity) &&
            view.Find("homeDetails").Get(Ui.Opacity) >= view.Find("launchActions").Get(Ui.Opacity) &&
            view.Find("homeToolbar").Get(Ui.Opacity) > view.Find("launchActions").Get(Ui.Opacity), "instance selection, details and launch actions enter in order");
        Check(view.Find("worldBlock").Get(Ui.Rotation) == 0, "the instance icon stays upright during entrance");
        Capture(host, "motion-home-150ms");
        Settle();
        Check(!view.Find("pageExit").Get(Ui.Visible) && view.Find("homeDetails").Get(Ui.TranslateY) == 0, "page releases its departing visual and settles exactly");
        var sidebar = view.Find("sidebar");
        float startIndicator = sidebar.Get(Sidebar.Properties.IndicatorY);
        model.Library(); Frame(0); Frame(.06);
        float middleIndicator = sidebar.Get(Sidebar.Properties.IndicatorY);
        float endIndicator = sidebar.GetBaseValue(Sidebar.Properties.IndicatorY);
        Check(middleIndicator > startIndicator && middleIndicator < endIndicator, "navigation indicator connects the previous and selected section");
        Capture(host, "motion-navigation-060ms");
        Settle(); Check(sidebar.Get(Sidebar.Properties.IndicatorY) == endIndicator, "navigation indicator settles on the selected section");
        Check(sidebar.Get(Sidebar.Properties.IndicatorHeight) == view.Find("navLibrary").Bounds.Height,
            "navigation highlight uses the actual stacked button height");
        model.NewInstance(); Frame(0);
        Check(view.Find("create").Children.First().Get(Ui.TranslateX) > 24, "opening an instance editor enters one level deeper");
        Frame(.06); Capture(host, "motion-detail-060ms"); Settle();
        model.Home(); Frame(0);
        Check(view.Find("homeDetails").Get(Ui.Opacity) == 0, "returning from a detail page starts a fresh home entrance");
        Settle();

        var play = view.Find("play");
        var playBounds = UiTransform.VisualBounds(play);
        host.Send(new(InputType.Move, playBounds.X + 25, playBounds.Y + 20, 0)); Settle();
        host.Send(new(InputType.Down, playBounds.X + 25, playBounds.Y + 20, 0)); Frame(0); Frame(.035);
        Check(play.Get(Ui.ScaleX) is > .97f and < 1 && play.Get(Ui.ScaleY) == play.Get(Ui.ScaleX), "primary play action gives a clear press response");
        Frame(.1); Check(play.Get(Ui.ScaleX) == .97f, "pressed play action holds its feedback without a bounce");
        host.Send(new(InputType.Move, -1, -1, 0)); host.Send(new(InputType.Up, -1, -1, 0)); Frame(0); Frame(.06);
        Check(play.Get(Ui.ScaleX) is > .97f and < 1 && model.Page == "home", "cancelled press returns smoothly without invoking play");
        Settle(); Check(play.Get(Ui.ScaleX) == 1, "primary action returns to its original size");
        var hero = view.Find("hero");
        var heroBounds = UiTransform.VisualBounds(hero);
        var icon = view.Find("worldBlock");
        var iconBounds = UiTransform.VisualBounds(icon);
        Frame(30);
        Check(!view.Animations.NeedsFrames && view.Animations.Animations.Count == 0, "idle home has no ambient animation frames");
        host.Send(new(InputType.Move, heroBounds.X + heroBounds.Width / 2, heroBounds.Y + heroBounds.Height / 2, 0)); Settle();
        Check(UiTransform.VisualBounds(hero) == heroBounds && UiTransform.VisualBounds(icon) == iconBounds && icon.Get(Ui.Rotation) == 0,
            "hovering the home image keeps its artwork and hit area stationary");
        Check(!view.Animations.NeedsFrames && view.Animations.Animations.Count == 0, "home image hover starts no decorative loop");
        host.Send(new(InputType.Move, -1, -1, 0)); Settle();
        Check(UiTransform.VisualBounds(hero) == heroBounds && view.Animations.Animations.Count == 0, "leaving the home image preserves the resting composition");

        model.ShowChoices("Motion dialog", [new() { Title = "Choice" }]); Frame(0);
        var dialog = view.Find("dialog");
        var dialogBounds = UiTransform.VisualBounds(dialog);
        Check(dialog.Get(Ui.Opacity) == 0 && dialog.Get(Ui.ScaleX) < 1 && dialog.Get(Ui.TranslateY) > 16, "dialog enters with depth above its backdrop");
        Frame(.05);
        Check(dialog.Get(Ui.Opacity) is > 0 and < 1 && UiTransform.VisualBounds(dialog).Y < dialogBounds.Y && dialog.Get(Ui.TranslateY) > 0, "dialog movement and opacity progress together");
        Capture(host, "motion-dialog-050ms");
        var close = UiTransform.VisualBounds(view.Find("dialogClose"));
        host.Click(close.X + close.Width / 2, close.Y + close.Height / 2); Frame(0);
        Check(!model.DialogOpen && view.Find("dialogExit").Get(Ui.Visible) && !view.Find("dialogExit").Get(Ui.Enabled), "closing commits immediately while a non-interactive dialog visual departs");
        Frame(.08); Capture(host, "motion-dialog-exit-080ms");
        Check(view.Find("dialogExit").Get(Ui.Opacity) is > 0 and < 1 && view.Find("dialogGhost").Get(Ui.TranslateY) > 0, "dialog has a real closing transition");
        Settle();
        Check(!model.DialogOpen && dialog.Get(Ui.Opacity) == 1 && dialog.Get(Ui.TranslateY) == 0 && !view.Animations.NeedsFrames,
            "clicking a moving dialog uses its visual position and releases its animations");
        model.ShowChoices("First", [new() { Title = "Choice" }]); Frame(0); Frame(.03);
        model.CloseDialog(); model.ShowChoices("Second", [new() { Title = "Another choice" }]); Frame(0); Settle();
        Check(model.DialogTitle == "Second" && dialog.Get(Ui.TranslateY) == 0 && !view.Animations.NeedsFrames,
            "immediate dialog replacement cannot retain the previous entrance offset");
        model.CloseDialog(); Frame(0); Frame(.04);
        model.ShowChoices("Reopened", [new() { Title = "Current choice" }]); Frame(0); Settle();
        Check(model.DialogOpen && !view.Find("dialogExit").Get(Ui.Visible), "reopening cancels the departing dialog before drawing the new one");
        model.CloseDialog(); Settle();

        model.Notice("Готово"); Frame(0); Frame(.04);
        Check(view.Find("notice").Get(Ui.TranslateY) is > -10 and < 0, "new operation feedback enters briefly");
        Settle(); model.DismissMessage(); Settle();

        var busy = new TaskCompletionSource();
        var work = model.WithBusy(() => busy.Task); Frame(0); Settle();
        Check(view.Find("activityDot").Get(Ui.Visible) && !view.Animations.NeedsFrames, "work status is visible without a repeating pulse");
        busy.SetResult(); context.Drain(); Settle();
        Check(work.IsCompleted && !view.Find("activityDot").Get(Ui.Visible), "work indicator follows the actual operation");

        host.Send(new(InputType.Move, -1, -1, 0)); Settle();
        for (int i = 0; i < 24; i++)
        {
            if (i % 2 == 0) model.Library(); else model.Home();
            Frame(.008);
            Check(view.Animations.Animations.Count(a => a.Name == "page" && !a.IsKilled) <= 1 &&
                view.Animations.Animations.Count(a => a.Name == "navigation" && !a.IsKilled) <= 1, "rapid navigation replaces the previous page and indicator animations");
        }
        Settle();
        Check(!view.Animations.NeedsFrames && view.Animations.Animations.Count == 0, "rapid navigation leaves no animation backlog: " + string.Join(", ", view.Animations.Animations.Select(a => $"{a.Name} active={a.IsActive} time={a.Time:F3}/{a.TotalDuration:F3}")));

        model.Library(); Frame(0); Frame(.04);
        host.Resize(1000, 720); Frame(0);
        Check(!view.Find("pageExit").Get(Ui.Visible), "resizing discards a departing page captured at the old window size");
        host.Resize(1180, 800); Settle();
        model.EditInstance(model.SelectedInstance!.Id); Frame(0); Settle();
        model.SelectInstanceTab("appearance"); Frame(0);
        Check(view.Find("header").Get(Ui.Opacity) == 1 && view.Find("instanceTabs").Get(Ui.Opacity) == 1, "switching tabs keeps the heading and tab controls still");
        Settle();

        model.Library(); model.LibraryItems.Clear();
        int invoked = 0;
        for (int i = 0; i < 80; i++) model.LibraryItems.Add(new()
        {
            Id = "motion-" + i, Title = "Item " + i, Primary = "Open item", HasProgress = true, Action = () => invoked++
        });
        Frame(0); Settle();
        var list = view.Find("instanceList");
        Element Row() => list.DescendantsAndSelf().First(n => n.Classes.Contains("entry") && n.Bounds.Y >= list.Bounds.Y && n.Bounds.Y < list.Bounds.Y + list.Bounds.Height);
        var row = Row();
        var button = row.DescendantsAndSelf().First(n => n.Type.Is(UiTypes.Button));
        var bounds = UiTransform.VisualBounds(button);
        float x = bounds.X + bounds.Width / 2, y = bounds.Y + bounds.Height / 2;
        var normal = button.Get(Ui.Background);
        host.Send(new(InputType.Move, x, y, 0)); Frame(.04);
        var hoverMiddle = button.Get(Ui.Background); Settle();
        var hover = button.Get(Ui.Background);
        Check(hoverMiddle != normal && hoverMiddle != hover, "button hover uses a short color transition");
        Check(UiTransform.VisualBounds(button) == bounds, "hover leaves button geometry unchanged");
        host.Send(new(InputType.Down, x, y, 0)); Frame(.04);
        Check(button.Get(Ui.Background) != hover && UiTransform.VisualBounds(button) == bounds, "press gives color feedback without shrinking or jumping");
        host.Send(new(InputType.Up, x, y, 0)); Settle();
        Check(invoked == 1, "stationary button invokes once");
        var progress = row.DescendantsAndSelf().First(n => n.Type.Is(UiTypes.ProgressBar));
        model.LibraryItems[0].Progress = 80; model.LibraryItems[0].Changed(); Frame(0); Frame(.08);
        Check(progress.Get(Ui.Progress) is > 0 and < 80, "download progress still interpolates real updates");
        Settle(); Check(progress.Get(Ui.Progress) == 80, "download progress reaches the reported value");
        Capture(host, "calm-virtual-cards");

        for (int i = 0; i < 12; i++) { UiVirtualList.ScrollIntoView(list, i % 2 == 0 ? 60 : 0, ScrollAlignment.Start); Frame(.02); }
        UiVirtualList.ScrollIntoView(list, 60, ScrollAlignment.Start); Frame(0); Settle();
        row = Row(); button = row.DescendantsAndSelf().First(n => n.Type.Is(UiTypes.Button));
        bounds = UiTransform.VisualBounds(button);
        host.Click(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2); Settle();
        Check(invoked == 2 && UiTransform.VisualBounds(button) == bounds, "recycled rows keep correct stationary click targets");
        Check(!view.Animations.NeedsFrames && view.Animations.Animations.Count == 0, "scrolling does not leave animation work");
        ListMotionChecks(model, presentation, host, Frame);

        model.Home(); Frame(.05); reduced = true; model.Animations = false; Frame(0);
        Check(new[] { "homeToolbar", "homeDetails", "launchActions" }.All(name =>
                view.Find(name).Get(Ui.Opacity) == 1 && view.Find(name).Get(Ui.TranslateY) == 0 && view.Find(name).Get(Ui.TranslateX) == 0) &&
            !view.Find("pageExit").Get(Ui.Visible) && !view.Animations.NeedsFrames, "disabling motion clears active transforms and departing visuals immediately");
        model.Animations = true; reduced = false; model.RefreshLibrary(); Settle();
        Check(!view.Animations.NeedsFrames, "enabling transitions does not restart decorative effects");
    }
}
