using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.System;
using Windows.UI.Composition;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.WinRT;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;
using ShadUI.Extensions;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using WinRT;
using ComObject = SharpGen.Runtime.ComObject;
using IVisualElement = Everywhere.Interop.IVisualElement;
using Vector = Avalonia.Vector;
using Visual = Windows.UI.Composition.Visual;

namespace Everywhere.Windows.Interop;

public sealed partial class Direct3D11ScreenCapture : IVisualElement.ICapturedBitmapData
{
    public PixelFormat Format => PixelFormat.Bgra8888;
    public AlphaFormat AlphaFormat => AlphaFormat.Premul;
    public nint Data { get; private set; }
    public PixelSize Size { get; private set; }
    public Vector Dpi => new(96, 96);
    public int Stride { get; private set; }

    private readonly IDCompositionDevice2? _dCompositionDevice2;
    private readonly nint _hThumbnailId;
    private readonly IDCompositionVisual2? _dCompositionVisual;
    private readonly ID3D11Device? _d3D11Device;
    private readonly IDirect3DDevice? _direct3DDevice;
    private readonly Direct3D11CaptureFramePool? _framePool;
    private readonly GraphicsCaptureSession? _session;

    private ID3D11Texture2D? _stagingTexture;
    private bool _disposed;
    private int _frameReceived;

    private static readonly DispatcherQueueController DispatcherQueueController;

    static Direct3D11ScreenCapture()
    {
        PInvoke.CreateDispatcherQueueController(
            new DispatcherQueueOptions
            {
                apartmentType = DISPATCHERQUEUE_THREAD_APARTMENTTYPE.DQTAT_COM_STA,
                threadType = DISPATCHERQUEUE_THREAD_TYPE.DQTYPE_THREAD_CURRENT,
                dwSize = (uint)Unsafe.SizeOf<DispatcherQueueOptions>()
            },
            out DispatcherQueueController).ThrowOnFailure();
    }

    // https://blog.adeltax.com/dwm-thumbnails-but-with-idcompositionvisual/
    // https://gist.github.com/ADeltaX/aea6aac248604d0cb7d423a61b06e247
    private Direct3D11ScreenCapture(nint sourceHWnd, nint targetHWnd, PixelRect relativeRect)
    {
        try
        {
            // Create the composition device
            var interopCompositorFactory = Compositor.As<IInteropCompositorFactoryPartner>();
            var pInteropCompositor = interopCompositorFactory.CreateInteropCompositor(0, 0, typeof(IDCompositionDevice2).GUID);
            _dCompositionDevice2 = ComObject.As<IDCompositionDevice2>(pInteropCompositor);

            DwmpQueryWindowThumbnailSourceSize((HWND)sourceHWnd, false, out var srcSize).ThrowOnFailure();
            if (srcSize.Width == 0 || srcSize.Height == 0)
            {
                throw new InvalidOperationException("Failed to query thumbnail source size.");
            }

            // Create the shared thumbnail visual
            var thumbProperties = new DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = DwmThumbnailPropertyFlags.RectDestination | DwmThumbnailPropertyFlags.Visible,
                rcDestination = new RECT(0, 0, srcSize.Width, srcSize.Height),
                fVisible = true,
            };
            DwmpCreateSharedThumbnailVisual(
                (HWND)targetHWnd,
                (HWND)sourceHWnd,
                2, // Undocumented flag
                ref thumbProperties,
                pInteropCompositor,
                out var pDCompositionVisual,
                out _hThumbnailId).ThrowOnFailure();
            _dCompositionVisual = new IDCompositionVisual2(pDCompositionVisual);

            // Transform and crop the visual using relativeRect
            using var containerVisual = _dCompositionDevice2.CreateVisual();
            containerVisual.AddVisual(_dCompositionVisual, true, null);

            // Create a transform matrix for translation
            using var transform = _dCompositionDevice2.CreateMatrixTransform();
            var matrix = Matrix3x2.CreateTranslation(-relativeRect.X, -relativeRect.Y);
            transform.SetMatrix(ref matrix);
            _dCompositionVisual.SetTransform(transform);

            // Set the clip region
            containerVisual.SetClip(new RawRectF(0, 0, relativeRect.Width, relativeRect.Height));

            var visual = Visual.FromAbi(containerVisual.NativePointer);
            visual.Size = new Vector2(relativeRect.Width, relativeRect.Height);

            // Create D3D device and frame pool
            _d3D11Device = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport);
            using var dxgiDevice = _d3D11Device.QueryInterface<IDXGIDevice>();
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var pD3D11Device));
            _direct3DDevice = MarshalInterface<IDirect3DDevice>.FromAbi(pD3D11Device);

            _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _direct3DDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                2, // Use a buffer of 2 to avoid capture lag
                new SizeInt32(relativeRect.Width, relativeRect.Height));

            var item = GraphicsCaptureItem.CreateFromVisual(visual);
            _session = _framePool.CreateCaptureSession(item);
            _session.IsCursorCaptureEnabled = false;

            // Do nothing but keep the DispatcherQueueController alive
            GC.KeepAlive(DispatcherQueueController);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private async Task CaptureFrameAsync(CancellationToken cancellationToken)
    {
        if (_framePool is null || _d3D11Device is null || _session is null || _dCompositionDevice2 is null)
            throw new InvalidOperationException("Capture session is not properly initialized.");

        var tcs = new TaskCompletionSource();
        await using var registration = cancellationToken.Register(() => tcs.TrySetCanceled());

        _framePool.FrameArrived += (f, _) =>
        {
            if (_disposed || Interlocked.Exchange(ref _frameReceived, 1) != 0) return;

            using var frame = f.TryGetNextFrame();
            if (frame is null) return;

            try
            {
                // Get the underlying ID3D11Texture2D from the frame's
                var access = frame.Surface.As<IDirect3DDxgiInterfaceAccess>();

                var textureGuid = typeof(ID3D11Texture2D).GUID;
                var pTexture = access.GetInterface(textureGuid);
                using var sourceTexture = new ID3D11Texture2D(pTexture);

                var desc = sourceTexture.Description;
                _stagingTexture = _d3D11Device.CreateTexture2D(
                    new Texture2DDescription
                    {
                        Width = desc.Width,
                        Height = desc.Height,
                        ArraySize = 1,
                        BindFlags = BindFlags.None,
                        Usage = ResourceUsage.Staging,
                        CPUAccessFlags = CpuAccessFlags.Read,
                        Format = desc.Format,
                        MipLevels = 1,
                        SampleDescription = new SampleDescription(1, 0),
                        MiscFlags = ResourceOptionFlags.None
                    });

                var immediateContext = _d3D11Device.ImmediateContext;
                immediateContext.CopyResource(_stagingTexture, sourceTexture);

                var mapBox = immediateContext.Map(_stagingTexture, 0);
                if (mapBox.DataPointer == 0)
                    throw new InvalidOperationException("Failed to map staging texture.");

                if (_disposed)
                {
                    immediateContext.Unmap(_stagingTexture, 0);
                    return;
                }

                Data = mapBox.DataPointer;
                Stride = (int)mapBox.RowPitch;
                Size = new PixelSize((int)desc.Width, (int)desc.Height);

                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        };

        _session.StartCapture();
        _dCompositionDevice2.Commit();

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (Data != 0 && _d3D11Device is not null && _stagingTexture is not null)
        {
            _d3D11Device.ImmediateContext.Unmap(_stagingTexture, 0);
        }

        if (_hThumbnailId != 0)
        {
            DwmUnregisterThumbnail(_hThumbnailId);
        }

        _stagingTexture?.Dispose();
        _direct3DDevice?.Dispose();
        _d3D11Device?.Dispose();

        Dispatcher.UIThread.InvokeOnDemand(() =>
        {
            _session?.Dispose();
            _framePool?.Dispose();
            _dCompositionVisual?.Dispose();
            _dCompositionDevice2?.Dispose();
        });
    }

    public static async Task<IVisualElement.ICapturedBitmapData> CaptureAsync(
        nint sourceHWnd,
        PixelRect relativeRect,
        CancellationToken cancellationToken = default)
    {
        var screenCapture = await Dispatcher.UIThread.InvokeOnDemandAsync(() =>
        {
            var targetHWnd = Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop ?
                desktop.Windows.FirstOrDefault()?.TryGetPlatformHandle()?.Handle ??
                throw new InvalidOperationException("Failed to get main window handle.") :
                throw new InvalidOperationException("Unsupported application lifetime.");
            return new Direct3D11ScreenCapture(sourceHWnd, targetHWnd, relativeRect);
        });

        try
        {
            await screenCapture.CaptureFrameAsync(cancellationToken);
            return screenCapture;
        }
        catch
        {
            screenCapture.Dispose();
            throw;
        }
    }

    [Flags]
    private enum DwmThumbnailPropertyFlags : uint
    {
        RectDestination = 0x00000001,
        Visible = 0x00000008,
    }

    // ReSharper disable InconsistentNaming
    // ReSharper disable NotAccessedField.Local
    [StructLayout(LayoutKind.Sequential)]
    private struct DWM_THUMBNAIL_PROPERTIES
    {
        public DwmThumbnailPropertyFlags dwFlags;
        public RECT rcDestination;
        public RECT rcSource;
        public byte opacity;
        public BOOL fVisible;
        public BOOL fSourceClientAreaOnly;
    }
    // ReSharper restore InconsistentNaming
    // ReSharper restore NotAccessedField.Local

    [LibraryImport("d3d11.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    [DllImport("dwmapi.dll", CallingConvention = CallingConvention.Winapi, PreserveSig = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern HRESULT DwmUnregisterThumbnail([In] nint hThumbnailId);

    [DllImport("dwmapi.dll", CallingConvention = CallingConvention.Winapi, PreserveSig = true, EntryPoint = "#162")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern HRESULT DwmpQueryWindowThumbnailSourceSize(
        [In] HWND hWndSource,
        [In] BOOL fSourceClientAreaOnly,
        [Out] out SIZE pSize);

    [DllImport("dwmapi.dll", CallingConvention = CallingConvention.Winapi, PreserveSig = true, EntryPoint = "#147")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern HRESULT DwmpCreateSharedThumbnailVisual(
        [In] HWND hWndDestination,
        [In] HWND hWndSource,
        [In] uint thumbnailFlags,
        [In] ref DWM_THUMBNAIL_PROPERTIES thumbnailProperties,
        [In] nint pDCompositionDesktopDevice,
        [Out] out nint pDCompositionVisual,
        [Out] out nint hThumbnailId);
}