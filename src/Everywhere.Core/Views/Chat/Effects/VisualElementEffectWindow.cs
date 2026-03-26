using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;

namespace Everywhere.Views;

public class VisualElementEffectWindow : OverlayWindow
{
    public double Scale { get; private set; }

    private readonly VisualElementParticleHost _unmaskedHost;
    private readonly VisualElementParticleHost _maskedHost;

    private PixelRect _screenBounds;

    private readonly RectangleGeometry _maskScreenGeometry;
    private readonly Geometry _evaGeometry;
    private readonly DrawingBrush _opacityMaskBrush;

    public VisualElementEffectWindow()
    {
        var panel = new Panel();
        // Multi-particle scope usually fires ~20-30 particles, cap pool at 35
        _maskedHost = new VisualElementParticleHost(this, 35);
        // Single drag-drop animation rarely exceeds 1-2 particles, cap pool at 5
        _unmaskedHost = new VisualElementParticleHost(this, 5);
        panel.Children.Add(_maskedHost);
        panel.Children.Add(_unmaskedHost);
        Content = panel;

        _maskScreenGeometry = new RectangleGeometry();
        _evaGeometry = Geometry.Parse(
            "F1 M515,235C364,235 233,289 233,481 233,655 351,726 515,726L526,726C691,726 809,655 809,481 809,289 678,235 526,235");
        var combined = new CombinedGeometry(GeometryCombineMode.Exclude, _maskScreenGeometry, _evaGeometry);
        var featherBrush = new RadialGradientBrush
        {
            Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.5, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.5, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Colors.Transparent, 0.9),
                new GradientStop(Colors.Black, 1.0) // fully visible at the very outer edge
            }
        };

        var drawingGroup = new DrawingGroup
        {
            Children =
            {
                new GeometryDrawing { Geometry = combined, Brush = Brushes.Black },
                new GeometryDrawing { Geometry = _evaGeometry, Brush = featherBrush }
            }
        };

        _opacityMaskBrush = new DrawingBrush
        {
            Drawing = drawingGroup,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top,
            Stretch = Stretch.None
        };
    }

    public void AddParticle(Point startPoint,
        IParticleTargetTracker? targetTracker,
        object? startContent,
        object? endContent,
        Size startSize,
        bool masked)
    {
        if (masked) _maskedHost.SpawnParticle(startPoint, targetTracker, startContent, endContent, startSize);
        else _unmaskedHost.SpawnParticle(startPoint, targetTracker, startContent, endContent, startSize);
    }

    public void SetPlacement(Screen targetScreen)
    {
        _screenBounds = targetScreen.Bounds;
        Position = _screenBounds.Position;
        Scale = DesktopScaling; // we must set Position first to get the correct scaling factor
        Width = _screenBounds.Width / Scale;
        Height = _screenBounds.Height / Scale;
    }

    public Point ScreenPixelToLocal(PixelPoint screenPoint)
    {
        return new Point(
            (screenPoint.X - _screenBounds.X) / Scale,
            (screenPoint.Y - _screenBounds.Y) / Scale);
    }

    public void UpdateMask(PixelRect? evaRect)
    {
        if (!evaRect.HasValue)
        {
            _maskedHost.OpacityMask = null;
            return;
        }

        var localEvaRect = new Rect(
            (evaRect.Value.X - _screenBounds.X) / Scale,
            (evaRect.Value.Y - _screenBounds.Y) / Scale,
            evaRect.Value.Width / Scale,
            evaRect.Value.Height / Scale
        );
        var localScreenRect = new Rect(0, 0, _screenBounds.Width / Scale, _screenBounds.Height / Scale);

        if (!localScreenRect.Intersects(localEvaRect))
        {
            _maskedHost.OpacityMask = null;
            return;
        }

        _maskScreenGeometry.Rect = localScreenRect;

        // Ensure we center and scale the Eva geometry to fit its real bounds.
        // Eva's path original bounds are roughly X: 233 to 809, Y: 235 to 726 (W: 576, H: 491)
        var boundingBox = new Rect(233, 235, 576, 491);
        var scaleX = localEvaRect.Width / boundingBox.Width;
        var scaleY = localEvaRect.Height / boundingBox.Height;
        var translateX = localEvaRect.X - boundingBox.X * scaleX;
        var translateY = localEvaRect.Y - boundingBox.Y * scaleY;

        _evaGeometry.Transform = new MatrixTransform(
            Matrix.CreateScale(scaleX, scaleY) *
            Matrix.CreateTranslation(translateX, translateY));

        _maskedHost.OpacityMask = _opacityMaskBrush;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        e.Cancel = e is { IsProgrammatic: false, CloseReason: not WindowCloseReason.ApplicationShutdown and not WindowCloseReason.OSShutdown };

        base.OnClosing(e);
    }

    public void HandleHostIdle()
    {
        if (!_unmaskedHost.HasActiveParticles && !_maskedHost.HasActiveParticles) Hide();
    }
}