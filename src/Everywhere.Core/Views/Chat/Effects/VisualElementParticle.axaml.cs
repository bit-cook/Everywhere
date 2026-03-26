using Avalonia.Controls;
using Avalonia.Media;

namespace Everywhere.Views;

public interface IParticleTargetTracker
{
    bool TryGetTargetCenterOnScreen(out PixelPoint point);
    void OnParticleCompleted();
}

/// <summary>
/// Control-based particle with spring dynamics for position and time-based easing for visual morphing.
/// Single particles morph into their target control, while multi-particles maintain their size and just use physics.
/// Designed to be managed by a bounded object pool using Spawn / Recycle mechanics.
/// </summary>
public sealed partial class VisualElementParticle : UserControl
{
    private const double MorphDurationSec = 0.55;
    private const double MaxTimeoutSec = 0.7;

    private const double SpringStiffness = 180.0;
    private const double SpringDamping = 22.0;

    private const double MaxShadowBlur = 24.0;
    private const double MaxShadowOffset = 25.0;
    private const double BaseShadowBlur = 4.0;
    private const double BaseShadowOffset = 2.0;

    private readonly VisualElementEffectWindow _owner;
    private readonly DropShadowEffect _dropShadowEffect;

    private Point _startPosition;
    private IParticleTargetTracker? _targetTracker;
    private Size _startSize;

    private double _morphProgress;
    private double _velocityX;
    private double _velocityY;
    private Point _currentPosition;
    private double _currentSpeed;
    private Point _endPosition;
    private Size _endSize;
    private double _elapsedTimeSec;
    private bool _isCompleted;

    public VisualElementParticle(VisualElementEffectWindow owner)
    {
        _owner = owner;

        InitializeComponent();

        BackgroundBorder.Effect = _dropShadowEffect = new DropShadowEffect
        {
            Color = Colors.Black
        };
    }

    /// <summary>
    /// Spawns (initializes or re-initializes) the particle with the provided physics and content logic.
    /// Acts as the surrogate constructor when dequeuing from an object pool.
    /// </summary>
    public void Spawn(
        Point startPosition,
        IParticleTargetTracker? targetTracker,
        object? startContent,
        object? endContent,
        Size startSize)
    {
        _startPosition = startPosition;
        _targetTracker = targetTracker;
        StartContentPresenter.Content = startContent;
        EndContentPresenter.Content = endContent;
        _startSize = startSize;

        Width = _startSize.Width;
        Height = _startSize.Height;
        
        _morphProgress = 0.0;
        _velocityX = 0.0;
        _velocityY = 0.0;
        _elapsedTimeSec = 0.0;
        _isCompleted = false;

        // Force a synchronous measure to establish the end size representation before flight
        if (endContent is not null)
        {
            EndContentPresenter.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _endSize = EndContentPresenter.DesiredSize;
            EndContentPresenter.Width = _endSize.Width;
            EndContentPresenter.Height = _endSize.Height;
        }

        InitializeFlightDynamics();
    }

    /// <summary>
    /// Recycles the particle, severing strong references to expensive UI and image contents, 
    /// allowing it to rest idly in the memory pool without preventing GC of transient bounds.
    /// </summary>
    public void Recycle()
    {
        _targetTracker = null;
        StartContentPresenter.Content = null;
        EndContentPresenter.Content = null;
    }

    private void InitializeFlightDynamics()
    {
        _currentPosition = _startPosition;

        UpdateEndPosition();

        var dx = _endPosition.X - _startPosition.X;
        var dy = _endPosition.Y - _startPosition.Y;

        var dist = Math.Sqrt(dx * dx + dy * dy);
        if (dist < 1) dist = 1;

        var nx = -dy / dist;
        var ny = dx / dist;

        var lateralSpeed = Random.Shared.NextDouble() * 3000.0 - 1500.0;
        var forwardSpeed = Random.Shared.NextDouble() * 500.0;

        _velocityX = nx * lateralSpeed + (dx / dist) * forwardSpeed;
        _velocityY = ny * lateralSpeed + (dy / dist) * forwardSpeed;

        ApplyVisualState();
    }

    private void UpdateEndPosition()
    {
        if (_targetTracker?.TryGetTargetCenterOnScreen(out var endPointOnScreen) is true)
        {
            _endPosition = _owner.ScreenPixelToLocal(endPointOnScreen);
        }
    }

    public bool Update(double deltaTimeMs)
    {
        if (_isCompleted) return true;

        var dt = deltaTimeMs / 1000.0;
        if (dt <= 0) return false;

        _elapsedTimeSec += dt;
        _morphProgress = Math.Clamp(_elapsedTimeSec / MorphDurationSec, 0.0, 1.0);

        UpdateEndPosition();

        var diffX = _currentPosition.X - _endPosition.X;
        var diffY = _currentPosition.Y - _endPosition.Y;

        var forceX = -SpringStiffness * diffX - SpringDamping * _velocityX;
        var forceY = -SpringStiffness * diffY - SpringDamping * _velocityY;

        _velocityX += forceX * dt;
        _velocityY += forceY * dt;

        _currentPosition = new Point(_currentPosition.X + _velocityX * dt, _currentPosition.Y + _velocityY * dt);
        _currentSpeed = Math.Sqrt(_velocityX * _velocityX + _velocityY * _velocityY);

        var positionSettled = Math.Abs(diffX) < 1.0 && Math.Abs(diffY) < 1.0 && _currentSpeed < 15.0;
        if ((positionSettled && _morphProgress >= 1.0) || _elapsedTimeSec > MaxTimeoutSec)
        {
            _isCompleted = true;
            _targetTracker?.OnParticleCompleted();
        }

        ApplyVisualState();
        return _isCompleted;
    }

    private void ApplyVisualState()
    {
        var t = CubicEaseOut(_morphProgress);
        var elevation = Math.Sin(_morphProgress * Math.PI);

        var baseWidth = _startSize.Width + (_endSize.Width - _startSize.Width) * t;
        var baseHeight = _startSize.Height + (_endSize.Height - _startSize.Height) * t;

        var finalWidth = Math.Max(1.0, baseWidth);
        var finalHeight = Math.Max(1.0, baseHeight);

        Width = finalWidth;
        Height = finalHeight;

        Canvas.SetLeft(this, _currentPosition.X - finalWidth / 2);
        Canvas.SetTop(this, _currentPosition.Y - finalHeight / 2);

        var radius = 8.0 - (4.0 * t);
        RootBorder.CornerRadius = BackgroundBorder.CornerRadius = StartContentBorder.CornerRadius = new CornerRadius(Math.Max(3, radius));

        var currentShadowBlur = BaseShadowBlur + (MaxShadowBlur * elevation);
        var currentShadowOffsetY = BaseShadowOffset + (MaxShadowOffset * elevation);
        var shadowOpacity = 0.6 * elevation;

        _dropShadowEffect.BlurRadius = currentShadowBlur;
        _dropShadowEffect.OffsetY = currentShadowOffsetY;
        _dropShadowEffect.Opacity = shadowOpacity;

        StartContentPresenter.Opacity = Math.Min(2d - 2d * t, 1d);
        EndContentPresenter.Opacity = t;
    }

    private static double CubicEaseOut(double x)
    {
        var t = x - 1.0;
        return 1.0 + t * t * t;
    }
}