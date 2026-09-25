using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using MechanicaLauncher.Desktop;
using Nitidus;
using Nitidus.Native;

internal static partial class Program
{
    private static void ListMotionChecks(LauncherModel model, LauncherView presentation, HeadlessHost host, Action<double> frame)
    {
        var view = presentation.View;
        var list = view.Find("instanceList");
        void Settle() { frame(0); frame(.25); frame(.01); }
        void MoveTo(Element node)
        {
            var b = UiTransform.VisualBounds(node);
            host.Send(new(InputType.Move, b.X + b.Width / 2, b.Y + b.Height / 2, 0));
        }
        UiVirtualList.ScrollTo(list, 0); frame(0); Settle();
        MoveTo(list);
        host.Send(new(InputType.Wheel, 0, 0, -120));
        double firstOffset = UiVirtualList.Inspect(list).Offset;
        Check(firstOffset > 0, "wheel responds immediately");
        frame(.2);
        Check(UiVirtualList.Inspect(list).Offset == firstOffset, "scrolling stops with the wheel without an inertial tail");
        host.Send(new(InputType.Wheel, 0, 0, -120)); Settle();
        Check(UiVirtualList.Inspect(list).Offset > firstOffset, "successive wheel events retain native scrolling");
        host.Send(new(InputType.Wheel, 0, 0, 120)); Settle();
        Check(Math.Abs(UiVirtualList.Inspect(list).Offset - firstOffset) < 1, "wheel reverses without a delayed destination");
        Check(list.DescendantsAndSelf().Where(n => n.Classes.Contains("entry")).All(n => n.Get(Ui.Opacity) == 1 && n.Get(Ui.ScaleX) == 1), "scroll cards retain their scale and readability at the viewport edges");
        host.Send(new(InputType.Move, -1, -1, 0));
        model.LibraryItems.Clear();
        int invoked = -1;
        ItemModel Item(int id) => new()
        {
            Id = "large-" + id, Title = "Мир " + id,
            Meta = "Minecraft 1.21.1 · Fabric", Description = "Сборка с модами и сохранёнными мирами.",
            Primary = "Открыть мир", Action = () => invoked = id
        };
        for (int i = 0; i < 10_000; i++) model.LibraryItems.Add(Item(i));
        UiVirtualList.ScrollTo(list, 0); frame(0); Settle();
        var info = UiVirtualList.Inspect(list);
        Check(info.Count == 10_000 && info.Realized < 24, "ten thousand items only realize a small visible window");
        Check(!view.Animations.NeedsFrames, "large settled list does not request idle frames");
        int maxRealized = info.Realized, maxAnimations = 0;
        var timings = new List<double>();
        for (int batch = 0; batch < 30; batch++)
        {
            for (int i = 0; i < 40; i++) model.LibraryItems.Add(Item(10_000 + batch * 40 + i));
            UiVirtualList.ScrollIntoView(list, batch * 313, ScrollAlignment.Start);
            var started = Stopwatch.GetTimestamp(); frame(1d / 60);
            timings.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            info = UiVirtualList.Inspect(list);
            maxRealized = Math.Max(maxRealized, info.Realized);
            maxAnimations = Math.Max(maxAnimations, view.Animations.Animations.Count);
            Check(info.Count == model.LibraryItems.Count && info.Realized < 24, "streaming additions preserve virtualization");
            Check(!view.Animations.NeedsFrames, "streaming and scrolling create no decorative animation work");
        }
        UiVirtualList.ScrollIntoView(list, 10_800, ScrollAlignment.Start); frame(0); Settle();
        info = UiVirtualList.Inspect(list);
        var anchor = UiVirtualList.GetItemKey(list, info.FirstVisible);
        model.LibraryItems.Insert(0, Item(20_000)); frame(0); Settle();
        Check(Equals(anchor, UiVirtualList.GetItemKey(list, UiVirtualList.Inspect(list).FirstVisible)), "inserting above the viewport preserves the reading position");
        Check(model.LibraryItems.Count == 11_201, "live stream and insertion retain every item");
        Capture(host, "calm-large-list");

        UiVirtualList.ScrollIntoView(list, model.LibraryItems.Count - 1, ScrollAlignment.End); frame(0); Settle();
        model.LibraryItems.Add(Item(30_000));
        UiVirtualList.ScrollIntoView(list, model.LibraryItems.Count - 1, ScrollAlignment.End); frame(0); frame(.12);
        var added = UiVirtualList.GetRealizedItem(list, model.LibraryItems.Count - 1)!;
        var card = added.DescendantsAndSelf().Single(n => n.Classes.Contains("entry"));
        Check(card.Get(Ui.Opacity) == 1 && !view.Animations.NeedsFrames, "new content is immediately readable without an entrance delay");
        var button = added.DescendantsAndSelf().First(n => n.Type.Is(UiTypes.Button));
        var bounds = UiTransform.VisualBounds(button);
        float x = bounds.X + bounds.Width / 2, y = bounds.Y + bounds.Height / 2;
        host.Send(new(InputType.Down, x, y, 0));
        var cardBounds = UiTransform.VisualBounds(card); frame(.08);
        Check(UiTransform.VisualBounds(card) == cardBounds, "pressing new content keeps the card stationary");
        host.Send(new(InputType.Up, x, y, 0)); Settle();
        Check(invoked == 30_000, "immediate click on newly added content invokes the correct item");
        Check(UiTransform.VisualBounds(card) == cardBounds, "releasing the pointer keeps the card stationary");
        MoveTo(card); frame(0); frame(.12);
        Check(UiTransform.VisualBounds(card) == cardBounds, "hover does not tilt or lift a card");
        host.Send(new(InputType.Move, -1, -1, 0)); frame(0); Settle();
        Check(!view.Animations.NeedsFrames && view.Animations.Animations.Count == 0, "stress test leaves no animation work");
        Console.WriteLine($"List stability: {model.LibraryItems.Count:N0} items, peak realized {maxRealized}, peak animations {maxAnimations}; WARP median frame {timings.Order().ElementAt(timings.Count / 2):F1} ms.");
    }
}
