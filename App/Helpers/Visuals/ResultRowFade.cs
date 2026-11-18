using System.Windows;
using System.Windows.Media.Animation;
using System.Runtime.CompilerServices;

namespace Lertaro.App.Helpers.Visuals;

public static class ResultRowFade
{
    internal const int DurationMs = 150;
    private static readonly ConditionalWeakTable<object, object> AnimatedDataContexts = new();
    private static readonly object FadeMarker = new();

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(ResultRowFade),
        new PropertyMetadata(false, OnIsEnabledChanged));

    private static readonly DependencyProperty AnimationVersionProperty = DependencyProperty.RegisterAttached(
        "AnimationVersion",
        typeof(int),
        typeof(ResultRowFade),
        new PropertyMetadata(0));

    public static void SetIsEnabled(DependencyObject element, bool value) =>
        element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element) =>
        (bool)element.GetValue(IsEnabledProperty);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
            return;

        if ((bool)e.NewValue)
        {
            element.Loaded += OnRowLoaded;
            element.DataContextChanged += OnRowDataContextChanged;
        }
        else
        {
            element.Loaded -= OnRowLoaded;
            element.DataContextChanged -= OnRowDataContextChanged;
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = 1;
        }
    }

    private static void OnRowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
            FadeIfNew(element);
    }

    private static void OnRowDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is FrameworkElement { IsLoaded: true } element)
            FadeIfNew(element);
    }

    private static void FadeIfNew(FrameworkElement element)
    {
        if (element.DataContext is not { } dataContext)
            return;

        if (AnimatedDataContexts.TryGetValue(dataContext, out _))
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = 1;
            return;
        }

        AnimatedDataContexts.Add(dataContext, FadeMarker);
        FadeIn(element);
    }

    internal static void FadeIn(FrameworkElement element)
    {
        var version = (int)element.GetValue(AnimationVersionProperty) + 1;
        element.SetValue(AnimationVersionProperty, version);
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = 0;

        var animation = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(DurationMs));
        animation.Completed += (_, _) =>
        {
            if ((int)element.GetValue(AnimationVersionProperty) != version)
                return;

            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = 1;
        };
        element.BeginAnimation(UIElement.OpacityProperty, animation);
    }
}
