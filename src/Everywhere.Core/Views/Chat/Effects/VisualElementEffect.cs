using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Everywhere.Chat;
using Everywhere.Common;
using Everywhere.Interop;
using Microsoft.Extensions.Logging;

namespace Everywhere.Views;

/// <summary>
/// A high-performance, cross-monitor visual effect manager that orchestrates flying particle animations
/// from original screen positions into target UI attachments or regions within the ChatWindow. 
/// Designed as a singleton service.
/// </summary>
/// <remarks>
/// This system supports two distinctly handled animation modes:
/// 
/// 1. Single-Element Morphing (`RunOnceAsync`): 
///    Triggered when a user selects a specific visual element on screen. A snapshot is captured, 
///    and a UI particle dynamically morphs (fades and scales) from the raw image bounds into its 
///    final DataContext-bound destination (e.g., a `ChatAttachment` chip) while tracking the window.
///    
/// 2. Multi-Element Swarm (`Scope` / `VisualTreeBuilder`): 
///    Used during automated visual tree building. Employs a DPI-aware, batched TopLevel screenshot strategy
///    where hundreds of `IImage` sub-crops are fired sequentially based on a heuristic queue. 
///    The physics engine applies lateral scattering ("flocking") and Hooke's Law spring dynamics to 
///    absorb particles seamlessly behind the chatbot mascot (Eva). Masking is handled via a transparent Overlay window.
/// </remarks>
public sealed class VisualElementEffect(
    IVisualElementAnimationTarget animationTarget,
    ILogger<VisualElementEffect> logger
)
{
    private readonly IVisualElementAnimationTarget _animationTarget = animationTarget;
    private readonly List<VisualElementEffectWindow> _effectWindows = [];

    public async Task RunOnceAsync(IVisualElement visualElement, ChatAttachment chatAttachment)
    {
        try
        {
            ArrangeEffectWindows();
            if (_effectWindows.Count == 0)
            {
                chatAttachment.Opacity = 1d;
                return;
            }

            var (sourceBounds, startBitmap) = await Task.Run(async () =>
            {
                var bounds = visualElement.BoundingRectangle;
                if (bounds.Width <= 0 || bounds.Height <= 0)
                {
                    return (bounds, null);
                }

                return (bounds, await CreateStartBitmapAsync(visualElement));
            }).WaitAsync(TimeSpan.FromSeconds(3));
            if (startBitmap is null)
            {
                chatAttachment.Opacity = 1d;
                return;
            }

            foreach (var effectWindow in _effectWindows)
            {
                var sourceCenter = new PixelPoint(sourceBounds.Center.X, sourceBounds.Center.Y);
                var startPoint = effectWindow.ScreenPixelToLocal(sourceCenter);
                var startSize = new Size(
                    Math.Max(16, sourceBounds.Width / effectWindow.Scale),
                    Math.Max(16, sourceBounds.Height / effectWindow.Scale));

                var tracker = new RunOnceTracker(this, chatAttachment);
                effectWindow.AddParticle(
                    startPoint,
                    tracker,
                    startBitmap,
                    chatAttachment,
                    startSize,
                    false);
            }
        }
        catch
        {
            chatAttachment.Opacity = 1d;
        }
    }

    private static async Task<Bitmap?> CreateStartBitmapAsync(IVisualElement visualElement)
    {
        try
        {
            using var pointer = await visualElement.CaptureAsync(CancellationToken.None);
            return pointer.ToAvaloniaBitmap();
        }
        catch
        {
            return null;
        }
    }

    public Scope BeginScope(CancellationToken cancellationToken)
    {
        Dispatcher.UIThread.PostOnDemand(ArrangeEffectWindows);

        return new Scope(this, logger, cancellationToken);
    }

    private void ArrangeEffectWindows()
    {
        var screens = App.ScreenImpl.AllScreens;
        if (screens is not { Count: > 0 })
        {
            foreach (var effectWindow in _effectWindows) effectWindow.Close();
            _effectWindows.Clear();
            return;
        }

        var i = 0;
        for (; i < screens.Count; i++)
        {
            VisualElementEffectWindow effectWindow;
            if (_effectWindows.Count > i)
            {
                effectWindow = _effectWindows[i];
            }
            else
            {
                effectWindow = new VisualElementEffectWindow();
                _effectWindows.Add(effectWindow);
            }

            effectWindow.SetPlacement(screens[i]);
            effectWindow.Show();
            effectWindow.Topmost = true;
        }

        // Remove unnecessary VisualElementEffectWindow
        for (var j = _effectWindows.Count - 1; j >= i; j--)
        {
            _effectWindows[j].Close();
            _effectWindows.RemoveAt(j);
        }
    }

    /// <summary>
    /// Represents an asynchronous, scoped lifecycle for executing multi-particle swarm animations. 
    /// Instantiated by the <see cref="VisualTreeBuilder"/> when mapping a raw window's visual tree.
    /// Elements are logically evaluated by heuristic priorities and placed into a priority queue.
    /// 
    /// An active background loop continuously dequeues the highest-priority elements and emits masked particles.
    /// To ensure performance, a subset of lowest-priority elements naturally drops off queue processing.
    /// Because UI parsing typically processes significantly faster than visual particle emission, 
    /// disposing the <c>Scope</c> (<see cref="DisposeAsync"/>) elegantly awaits the completion of 
    /// the currently scattered particles, ensuring visual delivery concludes before yielding execution.
    /// </summary>
    public sealed class Scope : IParticleTargetTracker, IAsyncDisposable
    {
        private readonly PriorityQueue<IVisualElement, float> _queue = new(Comparer<float>.Create((a, b) => b.CompareTo(a)));
        private readonly Dictionary<string, Bitmap> _topLevelBitmaps = new();
        private readonly VisualElementEffect _owner;
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _cts;

        /// <summary>
        /// A heuristic cap on the expected number of emitted particles during a visual tree build.
        /// </summary>
        private const int ExpectedCount = 15;
        private int _emittedCount;
        private int _aliveParticlesCount;
        private volatile bool _isDisposed;

        public Scope(VisualElementEffect owner, ILogger logger, CancellationToken cancellationToken)
        {
            _owner = owner;
            _logger = logger;

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            EmissionLoopAsync(_cts.Token).Detach(IExceptionHandler.DangerouslyIgnoreAllException);
        }

        public void Add(IVisualElement element, float priority)
        {
            lock (_queue)
            {
                _queue.Enqueue(element, priority);
            }
        }

        private async Task EmissionLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var timeBeforeEmission = DateTimeOffset.Now;
                    IVisualElement? element = null;
                    lock (_queue)
                    {
                        if (_queue.Count > 0)
                        {
                            element = _queue.Dequeue();
                        }
                    }

                    if (element != null)
                    {
                        if (_owner._effectWindows.Count > 0)
                        {
                            await Dispatcher.UIThread.InvokeAsync(
                                () => EmitMaskedParticleAsync(element),
                                DispatcherPriority.Render,
                                cancellationToken);
                            _emittedCount++;
                        }
                    }

                    var delayMs = Random.Shared.Next(30, 70) - (int)(DateTimeOffset.Now - timeBeforeEmission).TotalMilliseconds;
                    if (delayMs > 0) await Task.Delay(delayMs, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unexpected error in VisualElementEffect emission loop");
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                while (_emittedCount < ExpectedCount)
                {
                    lock (_queue)
                    {
                        if (_queue.Count == 0)
                        {
                            break;
                        }
                    }

                    await Task.Delay(50);
                }

                await _cts.CancelAsync();
            }
            finally
            {
                _cts.Dispose();
                _isDisposed = true;

                Dispatcher.UIThread.Post(TryDisposeBitmaps);
            }
        }

        private void TryDisposeBitmaps()
        {
            if (!_isDisposed || _aliveParticlesCount != 0) return;

            foreach (var bitmap in _topLevelBitmaps.Values) bitmap.Dispose();
            _topLevelBitmaps.Clear();
        }

        private async Task<CroppedBitmap?> CreateStartImageAsync(IVisualElement element)
        {
            if (GetTopLevel(element) is not { } topLevel) return null;

            if (!_topLevelBitmaps.TryGetValue(topLevel.Id, out var topLevelBitmap))
            {
                try
                {
                    using var pointer = await Task.Run(() => topLevel.CaptureAsync(CancellationToken.None));
                    topLevelBitmap = pointer.ToAvaloniaBitmap();
                    _topLevelBitmaps[topLevel.Id] = topLevelBitmap;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to capture TopLevel for visual effect. Element: {ElementId}", element.Id);
                }
            }

            if (topLevelBitmap == null) return null;

            var elementBounds = element.BoundingRectangle;
            var topLevelBounds = topLevel.BoundingRectangle;

            // Compute relative rect
            var relativeRect = new PixelRect(
                elementBounds.X - topLevelBounds.X,
                elementBounds.Y - topLevelBounds.Y,
                elementBounds.Width,
                elementBounds.Height);

            // Intersect with toplevel so we don't go out of bounds of the image
            var bitmapRect = new PixelRect(0, 0, topLevelBitmap.PixelSize.Width, topLevelBitmap.PixelSize.Height);
            var renderScaling = (double)topLevelBitmap.PixelSize.Width / topLevelBounds.Width;

            var scaledRelativeRect = new PixelRect(
                (int)(relativeRect.X * renderScaling),
                (int)(relativeRect.Y * renderScaling),
                (int)(relativeRect.Width * renderScaling),
                (int)(relativeRect.Height * renderScaling));

            var intersect = scaledRelativeRect.Intersect(bitmapRect);
            if (intersect.Width <= 8 || intersect.Height <= 8) return null;

            return new CroppedBitmap(topLevelBitmap, intersect);
        }

        private static IVisualElement? GetTopLevel(IVisualElement current)
        {
            var node = current;
            while (node != null)
            {
                if (node.Type == VisualElementType.TopLevel) return node;
                node = node.Parent;
            }

            return null;
        }

        private async Task EmitMaskedParticleAsync(IVisualElement element)
        {
            try
            {
                if (element.States.HasFlag(VisualElementStates.Offscreen)) return;

                var bounds = element.BoundingRectangle;
                if (bounds.Width <= 8 || bounds.Height <= 8) return;

                var startImage = await CreateStartImageAsync(element);
                if (startImage is null) return;

                PixelRect? evaBounds = null;
                if (_owner._animationTarget.TryGetEvaBoundsOnScreen(out var rect))
                {
                    evaBounds = rect;
                }

                foreach (var effectWindow in _owner._effectWindows)
                {
                    effectWindow.UpdateMask(evaBounds);

                    var sourceCenter = new PixelPoint(bounds.Center.X, bounds.Center.Y);
                    var startPoint = effectWindow.ScreenPixelToLocal(sourceCenter);
                    var startSize = new Size(
                        Math.Max(16, bounds.Width / effectWindow.Scale),
                        Math.Max(16, bounds.Height / effectWindow.Scale));

                    _aliveParticlesCount++;
                    effectWindow.AddParticle(
                        startPoint,
                        this,
                        startImage,
                        null, // no end content for multi-particle, just physics
                        startSize,
                        true);
                }
            }
            catch (Exception)
            {
                _logger.LogWarning("Failed to emit visual element particle for element {ElementId}", element.Id);
            }
        }

        public bool TryGetTargetCenterOnScreen(out PixelPoint point)
        {
            if (_owner._animationTarget.TryGetEvaBoundsOnScreen(out var targetRect))
            {
                point = new PixelPoint(targetRect.Center.X, targetRect.Center.Y);
                return true;
            }

            point = default;
            return false;
        }

        public void OnParticleCompleted()
        {
            _aliveParticlesCount--;
            TryDisposeBitmaps();
        }
    }

    private sealed class RunOnceTracker(VisualElementEffect owner, ChatAttachment chatAttachment) : IParticleTargetTracker
    {
        public bool TryGetTargetCenterOnScreen(out PixelPoint point) =>
            owner._animationTarget.TryGetAttachmentCenterOnScreen(chatAttachment, out point);

        public void OnParticleCompleted()
        {
            chatAttachment.Opacity = 1d;
        }
    }
}