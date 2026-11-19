using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Lertaro.App.Services;
using Lertaro.PluginSdk.Abstractions.Plugins;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfCursors = System.Windows.Input.Cursors;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfPoint = System.Windows.Point;
using WpfSize = System.Windows.Size;

namespace Lertaro.App.Views.QuickSearchWindow.Helpers;

/// <summary>
/// The quick window's text-only type wheel. Every type owns one persistent text cell, so changing the
/// current type rotates the existing wheel instead of tearing it down and unfolding it again.
/// </summary>
internal sealed class CategoryOrbitPopup : IDisposable
{
    private const double SurfaceSize = 300;
    private const double PositionAOffset = 55;
    private const double OrbitRadius = 88;
    private const double HitWidth = 76;
    private const double HitHeight = 34;
    private static readonly TimeSpan StepDuration = TimeSpan.FromMilliseconds(200);

    private readonly Popup _popup;
    private readonly Canvas _surface;
    private readonly Canvas _wheelLayer;
    private readonly Ellipse _hitSurface;
    private readonly RotateTransform _wheelRotation = new();
    private readonly Dictionary<string, (Grid Host, TextBlock Text)> _labels =
        new(StringComparer.Ordinal);
    private IReadOnlyList<SidebarFilterItem> _categories = [];
    private SidebarFilterItem? _selectedCategory;
    private double _fontSize = 13;
    private WpfFontFamily _fontFamily = AppTypography.UiFont;
    private double _textOpacity = 0.4;
    private double _orbitCenterX = OrbitRadius + 38;
    private double _orbitCenterY = SurfaceSize / 2;
    private int _visibilityVersion;
    private bool _placementNudge;
    private bool _isCollapsing;
    private bool _disposed;

    public CategoryOrbitPopup()
    {
        _surface = new Canvas
        {
            Width = SurfaceSize,
            Height = SurfaceSize,
            Background = WpfBrushes.Transparent,
        };
        _wheelLayer = new Canvas
        {
            Width = SurfaceSize,
            Height = SurfaceSize,
            Background = null,
            RenderTransform = _wheelRotation,
        };
        _hitSurface = new Ellipse
        {
            Width = (OrbitRadius + 20) * 2,
            Height = (OrbitRadius + 20) * 2,
            Fill = WpfBrushes.Transparent,
            IsHitTestVisible = true,
        };
        _surface.Children.Add(_hitSurface);
        _surface.Children.Add(_wheelLayer);
        _surface.AddHandler(
            Mouse.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(OnPreviewMouseWheel),
            handledEventsToo: true);
        _surface.MouseLeftButtonUp += OnSurfaceLeftButtonUp;

        _popup = new Popup
        {
            Child = _surface,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.None,
            Placement = PlacementMode.Relative,
            StaysOpen = true,
            Focusable = false,
        };
    }

    public event Action<SidebarFilterItem>? CategorySelected;
    public event Action<int>? CategoryWheeled;

    public bool IsOpen => _popup.IsOpen && !_isCollapsing;
    public bool IsPointerOver => _surface.IsMouseOver;

    public bool ContainsScreenPoint(WpfPoint screenPoint)
    {
        if (!_popup.IsOpen)
            return false;

        var origin = _surface.PointToScreen(new WpfPoint(0, 0));
        var dx = screenPoint.X - (origin.X + _orbitCenterX);
        var dy = screenPoint.Y - (origin.Y + _orbitCenterY);
        var radius = OrbitRadius + 20;
        return dx * dx + dy * dy <= radius * radius;
    }

    public void RefreshPlacement()
    {
        if (!_popup.IsOpen)
            return;

        // A nominal offset change makes WPF recalculate Relative placement while the host window moves.
        _placementNudge = !_placementNudge;
        _popup.HorizontalOffset += _placementNudge ? 0.01 : -0.01;
    }

    public WpfPoint PositionAScreenPoint() =>
        _surface.PointToScreen(OrbitPoint(180));

    public void Show(
        FrameworkElement anchor,
        IReadOnlyList<SidebarFilterItem> categories,
        SidebarFilterItem selectedCategory,
        double fontSize,
        WpfFontFamily fontFamily,
        double textOpacity,
        double connectionY)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _fontSize = Math.Clamp(fontSize, 11, 18);
        _fontFamily = fontFamily;
        _textOpacity = Math.Clamp(textOpacity * 0.9, 0.25, 1);
        _categories = categories;
        _selectedCategory = selectedCategory;
        _isCollapsing = false;

        var positionAFromPopupLeft = PositionAOffset - anchor.ActualWidth / 2;
        _orbitCenterX = Math.Max(OrbitRadius + 18, positionAFromPopupLeft + OrbitRadius);
        _orbitCenterY = SurfaceSize / 2 + connectionY;
        _wheelRotation.CenterX = _orbitCenterX;
        _wheelRotation.CenterY = _orbitCenterY;
        _wheelRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        _wheelRotation.Angle = 0;
        _popup.PlacementTarget = anchor;
        // Relative placement measures this offset to the popup's right edge. Add the popup width so
        // its left edge lands just beyond the logo instead of mirrored over the search box.
        _popup.HorizontalOffset = anchor.ActualWidth + SurfaceSize + 8;
        _popup.VerticalOffset = (anchor.ActualHeight - SurfaceSize) / 2;

        ResetSurface();
        _popup.IsOpen = true;
        var version = ++_visibilityVersion;
        _surface.Dispatcher.BeginInvoke(
            new Action(() =>
            {
                if (version == _visibilityVersion && IsOpen)
                    LayoutOrbit(rotationSteps: 0, intro: true);
            }),
            DispatcherPriority.Loaded);
    }

    public void Update(
        IReadOnlyList<SidebarFilterItem> categories,
        SidebarFilterItem selectedCategory)
    {
        if (_disposed || !IsOpen)
            return;

        var oldIndex = IndexOf(_categories, _selectedCategory);
        var newIndex = IndexOf(categories, selectedCategory);
        var steps = oldIndex >= 0 && newIndex >= 0
            ? ShortestSteps(oldIndex, newIndex, categories.Count)
            : 0;

        _categories = categories;
        _selectedCategory = selectedCategory;
        _visibilityVersion++;
        LayoutOrbit(steps, intro: false);
    }

    public void Close()
    {
        if (_disposed)
            return;
        _visibilityVersion++;
        _isCollapsing = false;
        _popup.IsOpen = false;
    }

    public void CloseAnimated(Action? handOffFlight = null)
    {
        if (_disposed || !_popup.IsOpen || _isCollapsing)
            return;

        _isCollapsing = true;
        var version = ++_visibilityVersion;
        var pointA = OrbitPoint(180);
        if (_labels.Count == 0)
        {
            _popup.IsOpen = false;
            _isCollapsing = false;
            handOffFlight?.Invoke();
            return;
        }

        var selectedKey = _selectedCategory == null ? null : KeyOf(_selectedCategory);
        foreach (var (host, _) in _labels.Values)
        {
            if (string.Equals(host.Tag is SidebarFilterItem category ? KeyOf(category) : null,
                    selectedKey, StringComparison.Ordinal))
                continue;

            var centerX = Canvas.GetLeft(host) + host.DesiredSize.Width / 2;
            var centerY = Canvas.GetTop(host) + host.DesiredSize.Height / 2;
            var group = (TransformGroup)host.RenderTransform;
            var translate = (TranslateTransform)group.Children[0];
            var counterRotation = (RotateTransform)group.Children[1];
            var current = translate;
            var opacity = host.Opacity;
            host.BeginAnimation(UIElement.OpacityProperty, null);
            host.Opacity = opacity;
            translate.BeginAnimation(TranslateTransform.XProperty, null);
            translate.BeginAnimation(TranslateTransform.YProperty, null);
            counterRotation.BeginAnimation(RotateTransform.AngleProperty, null);
            counterRotation.Angle = 0;
            translate.X = current.X;
            translate.Y = current.Y;
            var easing = EaseOut();
            translate.BeginAnimation(
                TranslateTransform.XProperty,
                new DoubleAnimation(translate.X, pointA.X - centerX, StepDuration) { EasingFunction = easing });
            translate.BeginAnimation(
                TranslateTransform.YProperty,
                new DoubleAnimation(translate.Y, pointA.Y - centerY, StepDuration) { EasingFunction = easing });
            host.BeginAnimation(
                UIElement.OpacityProperty,
                new DoubleAnimation(opacity, 0, StepDuration) { EasingFunction = easing });
        }

        var timer = new DispatcherTimer { Interval = StepDuration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (version != _visibilityVersion)
                return;
            _popup.IsOpen = false;
            _isCollapsing = false;
            handOffFlight?.Invoke();
        };
        timer.Start();
    }

    private void LayoutOrbit(int rotationSteps, bool intro)
    {
        Canvas.SetLeft(_hitSurface, _orbitCenterX - _hitSurface.Width / 2);
        Canvas.SetTop(_hitSurface, _orbitCenterY - _hitSurface.Height / 2);

        var selectedIndex = IndexOf(_categories, _selectedCategory);
        if (selectedIndex < 0 || _categories.Count == 0)
            return;

        var wanted = _categories.Select(KeyOf).ToHashSet(StringComparer.Ordinal);
        foreach (var staleKey in _labels.Keys.Where(key => !wanted.Contains(key)).ToList())
        {
            _wheelLayer.Children.Remove(_labels[staleKey].Host);
            _labels.Remove(staleKey);
        }

        var step = 360d / _categories.Count;
        if (!intro && rotationSteps != 0)
        {
            var sweep = rotationSteps * step;
            var easing = EaseOut();
            _wheelRotation.BeginAnimation(RotateTransform.AngleProperty, null);
            _wheelRotation.Angle = 0;
            _wheelRotation.BeginAnimation(
                RotateTransform.AngleProperty,
                new DoubleAnimation(sweep, 0, StepDuration) { EasingFunction = easing });
        }

        for (var i = 0; i < _categories.Count; i++)
        {
            var category = _categories[i];
            var key = KeyOf(category);
            if (!_labels.TryGetValue(key, out var entry))
            {
                entry = CreateLabel(key);
                _labels.Add(key, entry);
                _wheelLayer.Children.Add(entry.Host);
            }

            entry.Host.Tag = category;
            entry.Text.Text = category.DisplayName;
            entry.Text.FontSize = _fontSize;
            entry.Text.FontFamily = _fontFamily;
            entry.Text.Opacity = _textOpacity;
            entry.Host.Measure(new WpfSize(double.PositiveInfinity, double.PositiveInfinity));

            var offset = (i - selectedIndex + _categories.Count) % _categories.Count;
            var angle = 180 + offset * step;
            var radians = angle * Math.PI / 180;
            var left = _orbitCenterX + Math.Cos(radians) * OrbitRadius - entry.Host.DesiredSize.Width / 2;
            var top = _orbitCenterY + Math.Sin(radians) * OrbitRadius - entry.Host.DesiredSize.Height / 2;
            Canvas.SetLeft(entry.Host, left);
            Canvas.SetTop(entry.Host, top);

            if (intro)
                AnimateIntro(entry.Host, angle, ReferenceEquals(category, _selectedCategory));
            else if (rotationSteps != 0)
                AnimateRotation(entry.Host, angle, rotationSteps * step);
        }
    }

    private (Grid Host, TextBlock Text) CreateLabel(string key)
    {
        var text = new TextBlock
        {
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");

        var host = new Grid
        {
            Width = HitWidth,
            Height = HitHeight,
            // A one-alpha surface is deliberately non-visible but guarantees the whole text cell,
            // rather than only its glyphs, participates in hit testing.
            Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(1, 0, 0, 0)),
            Cursor = WpfCursors.Hand,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
            IsHitTestVisible = true,
            RenderTransformOrigin = new WpfPoint(0.5, 0.5),
        };
        var transforms = new TransformGroup();
        transforms.Children.Add(new TranslateTransform());
        transforms.Children.Add(new RotateTransform());
        host.RenderTransform = transforms;
        host.Children.Add(text);
        TextOptions.SetTextFormattingMode(host, TextFormattingMode.Display);
        host.MouseEnter += (_, _) => text.SetResourceReference(TextBlock.ForegroundProperty, "AccentBlue");
        host.MouseLeave += (_, _) => text.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        host.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (_isCollapsing)
                return;

            var category = _categories.FirstOrDefault(item => string.Equals(KeyOf(item), key, StringComparison.Ordinal));
            if (category != null)
                CategorySelected?.Invoke(category);
        };
        return (host, text);
    }

    private void AnimateIntro(Grid host, double finalAngle, bool selected)
    {
        var group = (TransformGroup)host.RenderTransform;
        var translate = (TranslateTransform)group.Children[0];
        var counterRotation = (RotateTransform)group.Children[1];
        counterRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        counterRotation.Angle = 0;
        host.BeginAnimation(UIElement.OpacityProperty, null);
        if (selected)
        {
            translate.BeginAnimation(TranslateTransform.XProperty, null);
            translate.BeginAnimation(TranslateTransform.YProperty, null);
            translate.X = 0;
            translate.Y = 0;
            host.Opacity = 0;
            host.BeginAnimation(
                UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(1))
                {
                    BeginTime = StepDuration,
                });
            return;
        }

        var pointA = OrbitPoint(180);
        var finalPoint = OrbitPoint(finalAngle);
        translate.BeginAnimation(TranslateTransform.XProperty, null);
        translate.BeginAnimation(TranslateTransform.YProperty, null);
        translate.X = pointA.X - finalPoint.X;
        translate.Y = pointA.Y - finalPoint.Y;
        host.Opacity = 0;
        AnimateMotion(host, StepDuration, delay: StepDuration, fade: true);
    }

    private void AnimateRotation(Grid host, double finalAngle, double sweep)
    {
        host.BeginAnimation(UIElement.OpacityProperty, null);
        host.Opacity = 1;
        var group = (TransformGroup)host.RenderTransform;
        var translate = (TranslateTransform)group.Children[0];
        var counterRotation = (RotateTransform)group.Children[1];
        translate.BeginAnimation(TranslateTransform.XProperty, null);
        translate.BeginAnimation(TranslateTransform.YProperty, null);
        counterRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        translate.X = 0;
        translate.Y = 0;
        counterRotation.Angle = 0;

        var easing = EaseOut();
        counterRotation.BeginAnimation(
            RotateTransform.AngleProperty,
            new DoubleAnimation(-sweep, 0, StepDuration) { EasingFunction = easing });
    }

    private void AnimateMotion(Grid host, TimeSpan duration, TimeSpan delay, bool fade)
    {
        var easing = EaseOut();
        var transform = (TranslateTransform)((TransformGroup)host.RenderTransform).Children[0];
        transform.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(transform.X, 0, duration)
            {
                BeginTime = delay,
                EasingFunction = easing,
            });
        transform.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(transform.Y, 0, duration)
            {
                BeginTime = delay,
                EasingFunction = easing,
            });
        if (!fade)
            return;

        host.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, duration)
            {
                BeginTime = delay,
                EasingFunction = easing,
            });
    }

    private WpfPoint OrbitPoint(double angle)
    {
        var radians = angle * Math.PI / 180;
        return new WpfPoint(
            _orbitCenterX + Math.Cos(radians) * OrbitRadius,
            _orbitCenterY + Math.Sin(radians) * OrbitRadius);
    }

    private void OnSurfaceLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isCollapsing)
            return;

        var point = e.GetPosition(_surface);
        foreach (var (host, _) in _labels.Values)
        {
            var bounds = new Rect(
                Canvas.GetLeft(host),
                Canvas.GetTop(host),
                host.DesiredSize.Width,
                host.DesiredSize.Height);
            if (!bounds.Contains(point) || host.Tag is not SidebarFilterItem category)
                continue;

            e.Handled = true;
            CategorySelected?.Invoke(category);
            return;
        }

        e.Handled = true;
    }

    private void ResetSurface()
    {
        _labels.Clear();
        _wheelLayer.Children.Clear();
        _surface.Children.Clear();
        Canvas.SetLeft(_hitSurface, _orbitCenterX - _hitSurface.Width / 2);
        Canvas.SetTop(_hitSurface, _orbitCenterY - _hitSurface.Height / 2);
        _surface.Children.Add(_hitSurface);
        _surface.Children.Add(_wheelLayer);
    }

    private static string KeyOf(SidebarFilterItem category) =>
        string.IsNullOrEmpty(category.Id) ? "\0All" : category.Id;

    private static int IndexOf(
        IReadOnlyList<SidebarFilterItem> categories,
        SidebarFilterItem? selectedCategory)
    {
        if (selectedCategory == null)
            return -1;

        for (var i = 0; i < categories.Count; i++)
        {
            if (ReferenceEquals(categories[i], selectedCategory)
                || string.Equals(categories[i].Id, selectedCategory.Id, StringComparison.Ordinal))
                return i;
        }

        return -1;
    }

    private static int ShortestSteps(int from, int to, int count)
    {
        if (count <= 1)
            return 0;

        var forward = (to - from + count) % count;
        return forward <= count / 2 ? forward : forward - count;
    }

    private static CubicEase EaseOut() => new() { EasingMode = EasingMode.EaseOut };

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta == 0)
            return;
        if (_isCollapsing)
        {
            e.Handled = true;
            return;
        }

        CategoryWheeled?.Invoke(e.Delta > 0 ? -1 : 1);
        e.Handled = true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _visibilityVersion++;
        _isCollapsing = false;
        _popup.IsOpen = false;
        _surface.Children.Clear();
        _labels.Clear();
        CategorySelected = null;
        CategoryWheeled = null;
    }
}
