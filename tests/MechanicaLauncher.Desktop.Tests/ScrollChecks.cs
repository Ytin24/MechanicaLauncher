using System;
using System.Linq;
using MechanicaLauncher.Core.Instances;
using MechanicaLauncher.Core.Profiles;
using MechanicaLauncher.Desktop;
using Nitidus;
using Nitidus.Native;

internal static partial class Program
{
    private static void ScrollChecks()
    {
        var instances = new InstanceManager();
        var instance = instances.CreateInstance("Прокрутка", "1.21.1");
        using var model = new LauncherModel(new LauncherSettings { Username = "ScrollTest", Language = "ru", SelectedInstanceId = instance.Id, DiscordRpc = false, Animations = false }, instances);
        using var presentation = new LauncherView(model);
        using var host = new HeadlessHost(presentation.View, 840, 620);
        var view = presentation.View;
        double time = 0;
        void Render() { host.Render(time += 1); host.Render(time += 1); }
        var scroll = view.Find("pageScroll");
        var footer = view.Find("checkUpdates");
        bool InViewport(Element node, Element scroller)
        {
            var bounds = node.Bounds;
            var viewport = UiScroll.Inspect(scroller).Viewport;
            return bounds.Width > 0 && bounds.Height > 0 && bounds.X >= viewport.X && bounds.Y >= viewport.Y &&
                bounds.X + bounds.Width <= viewport.X + viewport.Width + 1 && bounds.Y + bounds.Height <= viewport.Y + viewport.Height + 1;
        }
        void Click(Element node) => host.Click(node.Bounds.X + node.Bounds.Width / 2, node.Bounds.Y + node.Bounds.Height / 2);
        void WheelToBottom(Element scroller)
        {
            var viewport = UiScroll.Inspect(scroller).Viewport;
            host.Send(new(InputType.Move, viewport.X + viewport.Width / 2, viewport.Y + viewport.Height / 2, 0));
            for (int i = 0; i < 40; i++) host.Send(new(InputType.Wheel, 0, 0, -120));
            Render();
        }
        foreach (bool light in new[] { false, true })
        {
            model.LightTheme = light;
            foreach (var size in new[] { (840u, 620u, 1f), (840u, 620u, 1.5f), (840u, 620u, 2f), (1180u, 800u, 1f) })
            {
                host.Resize((uint)(size.Item1 * size.Item3), (uint)(size.Item2 * size.Item3), size.Item3);
                model.Preferences(); UiScroll.ToTop(scroll); Render();
                var sidebar = view.Find("sidebar").Bounds;
                var header = view.Find("header").Bounds;
                Check(!InViewport(footer, scroll) && UiScroll.Inspect(scroll).VerticalBarVisible, "long settings show a scrollbar before the footer is visible");
                int invoked = 0;
                footer.Set(Ui.Command, Command.From(() => invoked++));
                Click(footer);
                Check(invoked == 0, "clipped footer cannot receive clicks outside the viewport");
                WheelToBottom(scroll);
                Check(InViewport(footer, scroll), $"settings footer is reachable at {size} in {(light ? "light" : "dark")} theme");
                Check(view.Find("caption").Bounds.Y == 0 && view.Find("sidebar").Bounds == sidebar && view.Find("header").Bounds == header, "scrolling keeps navigation and window controls fixed");
                Click(footer);
                Check(invoked == 1, "the revealed footer button receives its click");
                double offset = UiScroll.Inspect(scroll).VerticalOffset;
                model.Tick(); Render();
                Check(UiScroll.Inspect(scroll).VerticalOffset == offset, "status refresh preserves the scroll position");
                Capture(host, $"scroll-settings-{(light ? "light" : "dark")}-{size.Item1}x{size.Item2}-{size.Item3 * 100:0}pct");
                model.Home(); Render();
                var play = view.Find("play");
                var homeScroll = view.Find("heroTextScroll");
                Check(UiScroll.Inspect(homeScroll).VerticalOffset == 0 && InViewport(view.Find("instancePicker"), homeScroll) &&
                    !Visible(scroll) && Visible(play) && play.Bounds.Width > 0 && play.Bounds.Height > 0 &&
                    play.Bounds.X >= 0 && play.Bounds.X + play.Bounds.Width <= size.Item1 &&
                    play.Bounds.Y >= 0 && play.Bounds.Y + play.Bounds.Height <= size.Item2,
                    "returning home opens its details at the top and exposes the fixed launch action");
                model.Preferences(); Render();
                Check(UiScroll.Inspect(scroll).VerticalOffset == 0 && !InViewport(footer, scroll),
                    "reopening settings resets its own scroll position before showing the page");
            }
        }
        host.Resize(840, 620); model.LightTheme = false; model.Preferences(); Render();
        Click(view.Find("navSettings"));
        for (int i = 0; i < 80 && host.FocusedElement != footer; i++) { host.Key(9); Render(); }
        Check(host.FocusedElement == footer && InViewport(footer, scroll), "Tab reveals a focused button below the fold");

        model.EditInstance(instance.Id); model.ToggleAdvanced(); Render();
        var save = view.Find("saveInstance");
        Check(!InViewport(save, scroll), "advanced instance form exceeds the minimum height");
        WheelToBottom(scroll);
        Check(InViewport(save, scroll), "advanced instance save button is reachable");
        model.EditName = "Проверенная прокрутка"; Click(save); Render();
        Check(instances.GetInstance(instance.Id)!.Name == model.EditName, "scrolled instance form saves from its visible button");
        Capture(host, "scroll-instance-advanced");

        model.BeginDialog("Длинная форма", string.Join('\n', Enumerable.Repeat("Строка описания", 40)), "Готово", null); Render();
        var dialog = view.Find("dialogScroll");
        var accept = view.Find("dialogAccept");
        var headerBounds = view.Find("dialogHeader").Bounds;
        var footerBounds = view.Find("dialogFooter").Bounds;
        Check(UiScroll.Inspect(dialog).VerticalBarVisible && accept.Bounds.Y + accept.Bounds.Height <= 620, "long dialog keeps its actions visible before scrolling");
        WheelToBottom(dialog);
        Check(UiScroll.Inspect(dialog).VerticalOffset > 0 && view.Find("dialogHeader").Bounds == headerBounds && view.Find("dialogFooter").Bounds == footerBounds, "long dialog scrolls its content with fixed title and actions");
        model.BeginDialog("Следующая форма", string.Join('\n', Enumerable.Repeat("Новое описание", 40)), "Готово", null); Render();
        Check(UiScroll.Inspect(dialog).VerticalOffset == 0, "a new dialog starts at the top");
        WheelToBottom(dialog); Click(accept); Render();
        Check(!model.DialogOpen, "scrolled dialog action is clickable");
    }
}
