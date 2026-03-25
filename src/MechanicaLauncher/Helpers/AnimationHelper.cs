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
        el.RenderTransform = new TranslateTransform { Y = 14 };
        el.Opacity = 0;

        var sb = new Storyboard();

        var fade = new DoubleAnimation
        {
            From = 0, To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(400)),
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(fade, el);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var slide = new DoubleAnimation
        {
            From = 14, To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(400)),
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(slide, el.RenderTransform);
        Storyboard.SetTargetProperty(slide, "Y");

        sb.Children.Add(fade);
        sb.Children.Add(slide);
        sb.Begin();
    }

    public static void AddCardHover(Border card)
    {
        var normalBg = Windows.UI.Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF);
        var hoverBg = Windows.UI.Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF);
        var hoverBorder = Windows.UI.Color.FromArgb(0x40, 0x4C, 0xAF, 0x50);
        var normalBorder = Windows.UI.Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF);

        card.RenderTransform = new TranslateTransform();

        card.PointerEntered += (_, _) =>
        {
            card.Background = new SolidColorBrush(hoverBg);
            card.BorderBrush = new SolidColorBrush(hoverBorder);

            var sb = new Storyboard();
            var lift = new DoubleAnimation
            {
                To = -2, Duration = new Duration(TimeSpan.FromMilliseconds(200)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(lift, card.RenderTransform);
            Storyboard.SetTargetProperty(lift, "Y");
            sb.Children.Add(lift);
            sb.Begin();
        };

        card.PointerExited += (_, _) =>
        {
            card.Background = new SolidColorBrush(normalBg);
            card.BorderBrush = new SolidColorBrush(normalBorder);

            var sb = new Storyboard();
            var drop = new DoubleAnimation
            {
                To = 0, Duration = new Duration(TimeSpan.FromMilliseconds(200)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(drop, card.RenderTransform);
            Storyboard.SetTargetProperty(drop, "Y");
            sb.Children.Add(drop);
            sb.Begin();
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
            spring.FinalValue = Vector3.One;
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

        visual.CenterPoint = GetCenter(el);

        var pulse = compositor.CreateVector3KeyFrameAnimation();
        pulse.InsertKeyFrame(0f, Vector3.One);
        pulse.InsertKeyFrame(0.5f, new Vector3(1.018f, 1.018f, 1f),
            compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(0.6f, 1f)));
        pulse.InsertKeyFrame(1f, Vector3.One,
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
            : new Vector3(120, 28, 0);
}
