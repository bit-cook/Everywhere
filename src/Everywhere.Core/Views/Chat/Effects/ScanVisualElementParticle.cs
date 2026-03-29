using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Everywhere.Utilities;
using Serilog;
using SkiaSharp;

namespace Everywhere.Views;

public class ScanVisualElementParticle : VisualElementParticle
{
    private RefCountedSKImage? _windowMaskRef;
    private double _animationProgress;

    public override void Spawn(Point startPosition, IParticleTargetTracker? targetTracker, object? startContent, object? endContent, Size startSize)
    {
        _windowMaskRef = new RefCountedSKImage(startContent.NotNull<SKImage>());

        Width = startSize.Width;
        Height = startSize.Height;
        Canvas.SetLeft(this, startPosition.X - startSize.Width / 2d);
        Canvas.SetTop(this, startPosition.Y - startSize.Height / 2d);

        _animationProgress = 0d;
    }

    public override void Recycle()
    {
        DisposeCollector.DisposeToDefault(ref _windowMaskRef);
    }

    public override bool Update(double deltaTimeMs)
    {
        if (_windowMaskRef is null) return true;
        if (deltaTimeMs <= 0) return false;

        _animationProgress += deltaTimeMs / 1000d;
        InvalidateVisual();

        return _animationProgress >= 1.5d;
    }

    public override void Render(DrawingContext context)
    {
        if (_windowMaskRef is null) return;

        Console.WriteLine("Rendering scan particle at progress: " + _animationProgress);
        context.Custom(new FluidScanDrawOperation(
            this,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d));
    }

    private sealed class RefCountedSKImage(SKImage image) : IDisposable
    {
        private int _refCount = 1;

        public SKImage? Image { get; private set; } = image;

        public void AddRef()
        {
            if (Image != null)
            {
                Interlocked.Increment(ref _refCount);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Decrement(ref _refCount) != 0) return;

            Image?.Dispose();
            Image = null;
        }
    }

    /// <summary>
    /// A custom drawing operation that renders a fluid scanline effect over a target window bounds.
    /// It uses a captured screenshot of the window as an alpha mask to perfectly match the window's shape/rounded corners.
    /// </summary>
    private sealed class FluidScanDrawOperation : ICustomDrawOperation
    {
        private static readonly SKRuntimeEffect? FluidEffect;

        static FluidScanDrawOperation()
        {
            const string sksl =
                """
                uniform float2 u_resolution;
                uniform float u_time;
                uniform float u_progress; // Controls the vertical drop (0.0 to 1.0)
                uniform shader u_mask;    // The captured window screenshot for alpha masking

                const float SCAN_SPEED = 2.4;
                const float EVAPORATION_SPEED = 4.2;
                const float GLOW = 0.4;
                const float OPACITY = 0.7;

                // 2D rotation matrix
                float2x2 rot(float a) {
                    float s = sin(a), c = cos(a);
                    return float2x2(c, -s, s, c);
                }

                half4 main(float2 fragCoord) {
                    // Normalize coordinates and aspect ratio
                    float2 uv = fragCoord.xy / u_resolution.xy;
                    float2 st = uv;
                    st.x *= u_resolution.x / u_resolution.y;

                    // Fluid Dynamics with turbulence
                    float2 p = st * 3.0;
                    float t = u_time * SCAN_SPEED;
                    for(int i = 0; i < 3; i++) {
                        p = p * rot(1.5) + float2(sin(p.y + t), cos(p.x - t)) * 0.3;
                    }

                    // From Everywhere logo color
                    float3 color0 = float3(0.322, 0.690, 0.969);
                    float3 color1 = float3(0.929, 0.310, 0.710);
                    float3 color2 = float3(0.937, 0.886, 0.494);
                    float mix1 = sin(p.x * 0.8) * 0.5 + 0.5;
                    float mix2 = cos(p.y * 0.6) * 0.5 + 0.5;
                    float3 fluidColor = mix(color0, color1, mix1);
                    fluidColor = mix(fluidColor, color2, mix2);
                    fluidColor *= 1.1;

                    // Smudge Edges & Evaporation
                    float scanEdge = 1.2 - (u_progress * 1.4);
                    float edgeNoise = (sin(p.x * 2.0) + cos(p.y * 1.5)) * 0.08;
                    float dist = uv.y - scanEdge + edgeNoise;
                    float frontEdge = smoothstep(-0.15, 0.05, dist);
                    float evaporateTail = smoothstep(1.0, 0.0, dist * EVAPORATION_SPEED);
                    float alpha = frontEdge * evaporateTail * OPACITY;

                    float leadingEdgeGlow = smoothstep(-0.08, 0.0, dist) * smoothstep(0.18, 0.0, dist);
                    fluidColor += leadingEdgeGlow * float3(1.0, 0.9, 0.9) * GLOW;

                    half4 maskColor = u_mask.eval(fragCoord);
                    alpha *= smoothstep(0.4, 0.6, maskColor.a);
                    return half4(fluidColor * alpha, alpha); // premultiplied color
                }
                """;

            FluidEffect = SKRuntimeEffect.CreateShader(sksl, out var errors);
            if (FluidEffect == null)
            {
                Log.ForContext<FluidScanDrawOperation>().Error("Failed to compile SkSL shader: {Errors}", errors);
            }
        }

        private readonly ScanVisualElementParticle _owner;
        private readonly double _timeSecconds;
        private readonly Rect _bounds;
        private readonly RefCountedSKImage? _windowMaskRef;

        /// <summary>
        /// Creates a new frame of the fluid scan operation.
        /// </summary>
        /// <param name="owner"></param>
        /// <param name="timeSecconds">The continuously increasing time in seconds (for fluid swirling).</param>
        public FluidScanDrawOperation(ScanVisualElementParticle owner, double timeSecconds)
        {
            _owner = owner;
            _timeSecconds = timeSecconds;
            _bounds = new Rect(0d, 0d, owner.Width, owner.Height);
            _windowMaskRef = owner._windowMaskRef;
            _windowMaskRef?.AddRef();
        }

        public Rect Bounds => _bounds;

        public void Render(ImmediateDrawingContext context)
        {
            if (FluidEffect is null) return;
            if (_windowMaskRef is not { Image: { } windowMask }) return;

            var leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (leaseFeature is null) return;

            using var lease = leaseFeature.Lease();
            var canvas = lease.SkCanvas;

            canvas.Save();
            canvas.Translate((float)_bounds.X, (float)_bounds.Y);

            var scaleX = (float)(_bounds.Width / windowMask.Width);
            var scaleY = (float)(_bounds.Height / windowMask.Height);
            var localMatrix = SKMatrix.CreateScale(scaleX, scaleY);
            using var maskShader = windowMask.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, localMatrix);

            using var uniforms = new SKRuntimeEffectUniforms(FluidEffect);
            uniforms.Add("u_resolution", new[] { (float)_bounds.Width, (float)_bounds.Height });
            uniforms.Add("u_time", (float)_timeSecconds);
            uniforms.Add("u_progress", (float)_owner._animationProgress);

            using var children = new SKRuntimeEffectChildren(FluidEffect);
            children.Add("u_mask", maskShader);

            using var fluidShader = FluidEffect.ToShader(uniforms, children);

            using var paint = new SKPaint();
            paint.Shader = fluidShader;
            paint.IsAntialias = true;

            var drawRect = new SKRect(0, 0, (float)_bounds.Width, (float)_bounds.Height);
            canvas.DrawRect(drawRect, paint);

            canvas.Restore();
        }

        public bool HitTest(Point p) => false;

        public bool Equals(ICustomDrawOperation? other) => false;

        public void Dispose() => _windowMaskRef?.Dispose();
    }
}