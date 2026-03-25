using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace MechanicaLauncher.Helpers;

public static class AnimationHelper
{
    public static void SlideIn(UIElement el, int delayMs = 0)
    {
        el.Opacity = 0;
        el.RenderTransform = new TranslateTransform { Y = 16 };

        var sb = new Storyboard();

        var fade = new DoubleAnimation
        {
            From = 0, To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(350)),
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(fade, el);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var slide = new DoubleAnimation
        {
            From = 16, To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(350)),
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(slide, el.RenderTransform);
        Storyboard.SetTargetProperty(slide, "Y");

        sb.Children.Add(fade);
        sb.Children.Add(slide);
        sb.Begin();
    }

    public static void AddHover(UIElement el)
    {
        var visual = ElementCompositionPreview.GetElementVisual(el);
        var compositor = visual.Compositor;

        visual.CenterPoint = new System.Numerics.Vector3(
            (float)(el is FrameworkElement fe ? fe.ActualWidth / 2 : 100),
            (float)(el is FrameworkElement fe2 ? fe2.ActualHeight / 2 : 30),
            0);

        el.PointerEntered += (_, _) =>
        {
            visual.CenterPoint = new System.Numerics.Vector3(
                (float)(el is FrameworkElement f ? f.ActualWidth / 2 : 100),
                (float)(el is FrameworkElement f2 ? f2.ActualHeight / 2 : 30),
                0);

            var scaleAnim = compositor.CreateVector3KeyFrameAnimation();
            scaleAnim.InsertKeyFrame(1f, new System.Numerics.Vector3(1.015f, 1.015f, 1f),
                compositor.CreateCubicBezierEasingFunction(
                    new System.Numerics.Vector2(0.2f, 0f),
                    new System.Numerics.Vector2(0f, 1f)));
            scaleAnim.Duration = TimeSpan.FromMilliseconds(200);
            visual.StartAnimation("Scale", scaleAnim);

            if (el is Microsoft.UI.Xaml.Controls.Border border)
                border.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
        };

        el.PointerExited += (_, _) =>
        {
            var scaleAnim = compositor.CreateVector3KeyFrameAnimation();
            scaleAnim.InsertKeyFrame(1f, new System.Numerics.Vector3(1f, 1f, 1f),
                compositor.CreateCubicBezierEasingFunction(
                    new System.Numerics.Vector2(0.2f, 0f),
                    new System.Numerics.Vector2(0f, 1f)));
            scaleAnim.Duration = TimeSpan.FromMilliseconds(200);
            visual.StartAnimation("Scale", scaleAnim);

            if (el is Microsoft.UI.Xaml.Controls.Border border)
                border.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
        };
    }
}
