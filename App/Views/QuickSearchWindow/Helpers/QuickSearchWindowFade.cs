using System.Windows;
using System.Windows.Media.Animation;

namespace Lertaro.App.Views.QuickSearchWindow.Helpers;

/// <summary>
/// Fades the quick window's whole visual tree with WPF opacity.
/// </summary>
/// <remarks>
/// The native layered-window alpha timer ran, but this opaque software-rendered surface did not show its
/// intermediate frames. Animating the window root uses the same path as the result-list fade that works.
/// </remarks>
internal sealed class QuickSearchWindowFade : IDisposable
{
    internal const int DurationMs = 150;

    private readonly Lertaro.App.QuickSearchWindow _window;
    private int _version;

    internal QuickSearchWindowFade(Lertaro.App.QuickSearchWindow window) => _window = window;

    internal void PrepareShow()
    {
        _version++;
        _window.FadeRoot.BeginAnimation(UIElement.OpacityProperty, null);
        _window.FadeRoot.Opacity = 0;
    }

    internal void FadeIn(Action? completed = null)
    {
        var version = ++_version;
        var root = _window.FadeRoot;
        root.BeginAnimation(UIElement.OpacityProperty, null);
        var animation = new DoubleAnimation(root.Opacity, 1, TimeSpan.FromMilliseconds(DurationMs));
        animation.Completed += (_, _) =>
        {
            if (version != _version)
                return;
            root.BeginAnimation(UIElement.OpacityProperty, null);
            root.Opacity = 1;
            completed?.Invoke();
        };
        root.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    internal void FadeOut(Action completed)
    {
        var version = ++_version;
        var root = _window.FadeRoot;
        var animation = new DoubleAnimation(root.Opacity, 0, TimeSpan.FromMilliseconds(DurationMs));
        animation.Completed += (_, _) =>
        {
            if (version != _version)
                return;
            root.BeginAnimation(UIElement.OpacityProperty, null);
            root.Opacity = 0;
            completed();
        };
        root.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    internal static byte AlphaAt(int from, int to, long elapsedMs)
    {
        if (elapsedMs <= 0)
            return (byte)Math.Clamp(from, byte.MinValue, byte.MaxValue);
        if (elapsedMs >= DurationMs)
            return (byte)Math.Clamp(to, byte.MinValue, byte.MaxValue);

        var t = elapsedMs / (double)DurationMs;
        return (byte)Math.Clamp(
            (int)Math.Round(from + ((to - from) * t), MidpointRounding.AwayFromZero),
            byte.MinValue,
            byte.MaxValue);
    }

    public void Dispose()
    {
        _version++;
        _window.FadeRoot.BeginAnimation(UIElement.OpacityProperty, null);
        _window.FadeRoot.Opacity = 1;
    }
}
