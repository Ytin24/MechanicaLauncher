using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using System.Numerics;
using Windows.UI.ViewManagement;

namespace MechanicaLauncher.Helpers;

public static class AnimationHelper
{
    private static readonly UISettings UiSettings = new();

    public static void SlideIn(UIElement element, int delayMs = 0)
    {
        element.Opacity = 1;
        if (!UiSettings.AnimationsEnabled) return;

        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0), new Vector2(0.2f, 1));
        var duration = TimeSpan.FromMilliseconds(180);
        var delay = TimeSpan.FromMilliseconds(Math.Clamp(delayMs, 0, 45));

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0.88f);
        fade.InsertKeyFrame(1, 1, ease);
        fade.Duration = duration;
        fade.DelayTime = delay;
        fade.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        visual.StartAnimation("Opacity", fade);

        var slide = compositor.CreateScalarKeyFrameAnimation();
        slide.InsertKeyFrame(0, 3);
        slide.InsertKeyFrame(1, 0, ease);
        slide.Duration = duration;
        slide.DelayTime = delay;
        slide.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        visual.StartAnimation("Translation.Y", slide);
    }

    public static void AddCardHover(Border card)
    {
        var background = card.Background;
        var border = card.BorderBrush;
        card.BackgroundTransition = new BrushTransition { Duration = TimeSpan.FromMilliseconds(120) };

        void Reset()
        {
            card.BackgroundTransition.Duration = TimeSpan.FromMilliseconds(UiSettings.AnimationsEnabled ? 120 : 0);
            card.Background = background;
            card.BorderBrush = border;
            MoveTo(card, 0);
        }

        card.PointerEntered += (_, _) =>
        {
            card.BackgroundTransition.Duration = TimeSpan.FromMilliseconds(UiSettings.AnimationsEnabled ? 120 : 0);
            card.Background = (Brush)Application.Current.Resources["CardHoverBrush"];
            card.BorderBrush = (Brush)Application.Current.Resources["CardBorderBrush"];
            MoveTo(card, -1);
        };
        card.PointerExited += (_, _) => Reset();
        card.Unloaded += (_, _) =>
        {
            card.Background = background;
            card.BorderBrush = border;
            ResetPosition(card);
        };
    }

    public static void AddButtonFeedback(ButtonBase button)
    {
        void Update()
        {
            if (!button.IsLoaded) return;
            var offset = !button.IsEnabled ? 0 : button.IsPressed ? 1 : button.IsPointerOver ? -1 : 0;
            MoveTo(button, offset, button.IsPressed ? 70 : 120);
        }

        button.PointerEntered += (_, _) => Update();
        button.PointerExited += (_, _) => Update();
        button.RegisterPropertyChangedCallback(ButtonBase.IsPressedProperty, (_, _) => Update());
        button.IsEnabledChanged += (_, _) => Update();
        button.Unloaded += (_, _) => ResetPosition(button);
    }

    private static void MoveTo(UIElement element, float offset, int durationMs = 120)
    {
        if (!UiSettings.AnimationsEnabled)
        {
            ResetPosition(element);
            return;
        }

        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
        animation.InsertExpressionKeyFrame(0, "this.StartingValue");
        animation.InsertKeyFrame(1, offset, visual.Compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.2f, 0), new Vector2(0.2f, 1)));
        animation.Duration = TimeSpan.FromMilliseconds(durationMs);
        visual.StartAnimation("Translation.Y", animation);
    }

    private static void ResetPosition(UIElement element)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        if (visual.Properties.TryGetVector3("Translation", out _) != CompositionGetValueStatus.Succeeded) return;
        visual.StopAnimation("Translation.Y");
        visual.Properties.InsertVector3("Translation", Vector3.Zero);
    }
}
