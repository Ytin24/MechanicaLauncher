using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System.Numerics;

namespace MechanicaLauncher.Helpers;

public static class AnimationHelper
{
    public static void SlideIn(UIElement el, int delayMs = 0)
    {
        var visual = ElementCompositionPreview.GetElementVisual(el);
        var compositor = visual.Compositor;

        visual.Opacity = 0;
        visual.Offset = new Vector3(0, 14, 0);

        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, 1f, compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f)));
        fade.Duration = TimeSpan.FromMilliseconds(400);
        fade.DelayTime = TimeSpan.FromMilliseconds(delayMs);
        fade.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;

        var slide = compositor.CreateVector3KeyFrameAnimation();
        slide.InsertKeyFrame(0f, new Vector3(0, 14, 0));
        slide.InsertKeyFrame(1f, Vector3.Zero, compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f)));
        slide.Duration = TimeSpan.FromMilliseconds(400);
        slide.DelayTime = TimeSpan.FromMilliseconds(delayMs);
        slide.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;

        visual.StartAnimation("Opacity", fade);
        visual.StartAnimation("Offset", slide);
        batch.End();
    }

    public static void AddCardHover(Border card)
    {
        var originalBg = Windows.UI.Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF);
        var hoverBg = Windows.UI.Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF);
        var hoverBorder = Windows.UI.Color.FromArgb(0x33, 0x4C, 0xAF, 0x50);
        var normalBorder = Windows.UI.Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF);

        card.PointerEntered += (_, _) =>
        {
            card.Background = new SolidColorBrush(hoverBg);
            card.BorderBrush = new SolidColorBrush(hoverBorder);

            var visual = ElementCompositionPreview.GetElementVisual(card);
            var anim = visual.Compositor.CreateVector3KeyFrameAnimation();
            anim.InsertKeyFrame(1f, new Vector3(0, -2, 0),
                visual.Compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0f), new Vector2(0f, 1f)));
            anim.Duration = TimeSpan.FromMilliseconds(200);
            visual.StartAnimation("Offset", anim);
        };

        card.PointerExited += (_, _) =>
        {
            card.Background = new SolidColorBrush(originalBg);
            card.BorderBrush = new SolidColorBrush(normalBorder);

            var visual = ElementCompositionPreview.GetElementVisual(card);
            var anim = visual.Compositor.CreateVector3KeyFrameAnimation();
            anim.InsertKeyFrame(1f, Vector3.Zero,
                visual.Compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0f), new Vector2(0f, 1f)));
            anim.Duration = TimeSpan.FromMilliseconds(200);
            visual.StartAnimation("Offset", anim);
        };
    }

    public static void AddButtonSpring(UIElement btn)
    {
        var visual = ElementCompositionPreview.GetElementVisual(btn);
        var compositor = visual.Compositor;

        btn.PointerEntered += (_, _) =>
        {
            visual.CenterPoint = GetCenter(btn);
            var spring = compositor.CreateSpringVector3Animation();
            spring.FinalValue = new Vector3(1.06f, 1.06f, 1f);
            spring.DampingRatio = 0.6f;
            spring.Period = TimeSpan.FromMilliseconds(50);
            visual.StartAnimation("Scale", spring);
        };

        btn.PointerExited += (_, _) =>
        {
            var spring = compositor.CreateSpringVector3Animation();
            spring.FinalValue = new Vector3(1f, 1f, 1f);
            spring.DampingRatio = 0.6f;
            spring.Period = TimeSpan.FromMilliseconds(50);
            visual.StartAnimation("Scale", spring);
        };

        btn.PointerPressed += (_, _) =>
        {
            visual.CenterPoint = GetCenter(btn);
            var spring = compositor.CreateSpringVector3Animation();
            spring.FinalValue = new Vector3(0.94f, 0.94f, 1f);
            spring.DampingRatio = 0.5f;
            spring.Period = TimeSpan.FromMilliseconds(40);
            visual.StartAnimation("Scale", spring);
        };

        btn.PointerReleased += (_, _) =>
        {
            var spring = compositor.CreateSpringVector3Animation();
            spring.FinalValue = new Vector3(1.06f, 1.06f, 1f);
            spring.DampingRatio = 0.6f;
            spring.Period = TimeSpan.FromMilliseconds(50);
            visual.StartAnimation("Scale", spring);
        };
    }

    public static void StartBreathing(UIElement el)
    {
        var visual = ElementCompositionPreview.GetElementVisual(el);
        var compositor = visual.Compositor;

        if (el is FrameworkElement fe)
            fe.Loaded += (_, _) => visual.CenterPoint = GetCenter(el);

        var pulse = compositor.CreateVector3KeyFrameAnimation();
        pulse.InsertKeyFrame(0f, new Vector3(1f, 1f, 1f));
        pulse.InsertKeyFrame(0.5f, new Vector3(1.02f, 1.02f, 1f),
            compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(0.6f, 1f)));
        pulse.InsertKeyFrame(1f, new Vector3(1f, 1f, 1f),
            compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(0.6f, 1f)));
        pulse.Duration = TimeSpan.FromMilliseconds(3000);
        pulse.IterationBehavior = AnimationIterationBehavior.Forever;
        visual.StartAnimation("Scale", pulse);
    }

    public static void StopBreathing(UIElement el)
    {
        var visual = ElementCompositionPreview.GetElementVisual(el);
        visual.StopAnimation("Scale");
        visual.Scale = Vector3.One;
    }

    private static Vector3 GetCenter(UIElement el) =>
        el is FrameworkElement fe
            ? new Vector3((float)fe.ActualWidth / 2, (float)fe.ActualHeight / 2, 0)
            : new Vector3(100, 25, 0);
}
