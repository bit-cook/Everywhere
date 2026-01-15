using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using DynamicData;
using Everywhere.Interop;
using Everywhere.Views;
using ObjCRuntime;
using ZLinq;

namespace Everywhere.Mac.Interop;

internal abstract partial class ScreenSelectionSession : ScreenSelectionTransparentWindow
{
    protected IWindowHelper WindowHelper { get; }
    protected ScreenSelectionMaskWindow[] MaskWindows { get; }
    protected ScreenSelectionToolTipWindow ToolTipWindow { get; }

    protected ScreenSelectionMode CurrentMode { get; private set; }

    // Track current mouse location for updates
    protected CGPoint CurrentMouseLocation { get; private set; }

    protected IVisualElement? SelectedElement { get; private set; }

    private readonly IReadOnlyList<ScreenSelectionMode> _allowedModes;
    private readonly uint _windowNumber;
    private readonly CGRect _allScreenFrame;
    private readonly PixelRect _allScreenBounds;

    /// <summary>
    /// We reuse a single list to avoid allocations during picking.
    /// </summary>
    private readonly List<int> _windowOwnerPids = [];

    protected ScreenSelectionSession(IWindowHelper windowHelper, IReadOnlyList<ScreenSelectionMode> allowedModes, ScreenSelectionMode initialMode)
    {
        Debug.Assert(allowedModes.Count > 0);

        WindowHelper = windowHelper;
        _allowedModes = allowedModes;
        CurrentMode = initialMode;

        var handle = TryGetPlatformHandle()?.Handle ?? 0;
        if (handle != 0)
        {
            using var nsWindow = new NSWindow();
            nsWindow.Handle = handle;
            _windowNumber = (uint)nsWindow.WindowNumber;
            nsWindow.Handle = 0;
        }

        // Use NSScreen to get accurate logical bounds and handle scaling
        var allScreens = NSScreen.Screens;
        // The primary screen (index 0) defines the coordinate space origin (0,0) in Cocoa.
        // We need its height to convert between Cocoa (Bottom-Left) and Quartz (Top-Left) coordinates.
        var primaryScreen = allScreens[0];
        var primaryScreenHeight = primaryScreen.Frame.Height;

        MaskWindows = new ScreenSelectionMaskWindow[allScreens.Length];
        for (var i = 0; i < allScreens.Length; i++)
        {
            var screen = allScreens[i];
            var frame = screen.Frame;
            _allScreenFrame = CGRect.Union(_allScreenFrame, frame);

            // Convert to Avalonia coordinates (top-left origin)
            // Quartz Y = PrimaryScreenHeight - (Cocoa Y + Height)
            var bounds = new PixelRect(
                (int)frame.X,
                (int)(primaryScreenHeight - (frame.Y + frame.Height)),
                (int)frame.Width,
                (int)frame.Height);
            _allScreenBounds = _allScreenBounds.Union(bounds);
            var maskWindow = new ScreenSelectionMaskWindow(bounds);
            SetNsWindowPlacement(maskWindow, frame);
            windowHelper.SetHitTestVisible(maskWindow, false);
            MaskWindows[i] = maskWindow;
        }

        SetPlacement(_allScreenBounds, out _); // Place window to (0, 0) in avalonia way

        ToolTipWindow = new ScreenSelectionToolTipWindow(allowedModes, initialMode);
        windowHelper.SetHitTestVisible(ToolTipWindow, false);
        SetNsWindowPlacement(ToolTipWindow, null);
    }

    private static void SetNsWindowPlacement(Window window, CGRect? frame)
    {
        var handle = window.TryGetPlatformHandle()?.Handle ?? 0;
        if (handle == 0) return;

        using var nsWindow = new NSWindow();
        nsWindow.Handle = handle;
        nsWindow.Level = NSWindowLevel.ScreenSaver; // above all other windows
        if (frame.HasValue) nsWindow.SetFrame(frame.Value, true);
        nsWindow.Handle = 0;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // Place window to cover all screens
        // We must set after opened otherwise macOS may ignore the frame set in constructor
        SetNsWindowPlacement(this, _allScreenFrame);

        foreach (var maskWindow in MaskWindows) maskWindow.Show(this);
        ToolTipWindow.Show(this);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);

        e.Handled = true;
        e.Pointer.Capture(null);

        if (point.Properties.IsRightButtonPressed)
        {
            OnCanceled();
            Close();
            return;
        }

        if (point.Properties.IsLeftButtonPressed)
        {
            OnLeftButtonDown();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left) return;

        if (OnLeftButtonUp())
        {
            Close();
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        OnMouseWheel(Math.Sign(e.Delta.Y));
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                OnCanceled();
                Close();
                break;
            case Key.D1:
            case Key.NumPad1:
            case Key.F1:
                CurrentMode = ScreenSelectionMode.Screen;
                HandlePickModeChanged();
                break;
            case Key.D2:
            case Key.NumPad2:
            case Key.F2:
                CurrentMode = ScreenSelectionMode.Window;
                HandlePickModeChanged();
                break;
            case Key.D3:
            case Key.NumPad3:
            case Key.F3:
                CurrentMode = ScreenSelectionMode.Element;
                HandlePickModeChanged();
                break;
            case Key.D4:
            case Key.NumPad4:
            case Key.F4:
                CurrentMode = ScreenSelectionMode.Free;
                HandlePickModeChanged();
                break;
        }
        base.OnKeyDown(e);
    }

    private void OnMouseWheel(int delta)
    {
        var newIndex = _allowedModes.IndexOf(CurrentMode) + (delta > 0 ? -1 : 1);
        if (newIndex < 0) newIndex = _allowedModes.Count - 1;
        else if (newIndex >= _allowedModes.Count) newIndex = 0;
        CurrentMode = _allowedModes[newIndex];
        HandlePickModeChanged();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        HandlePointerMoved(CurrentMouseLocation = NSEvent.CurrentMouseLocation);
    }

    private void HandlePickModeChanged()
    {
        HandlePointerMoved(CurrentMouseLocation);
        Dispatcher.UIThread.Post(
            () => ToolTipWindow.ToolTip.Mode = CurrentMode,
            DispatcherPriority.Background);
    }

    private void HandlePointerMoved(CGPoint point)
    {
        // NSEvent.CurrentMouseLocation is in Cocoa coordinates (Bottom-Left origin).
        // We need to convert it to Quartz coordinates (Top-Left origin) for AX and WindowList.
        // The primary screen (index 0) defines the coordinate space origin.
        var primaryScreenHeight = NSScreen.Screens[0].Frame.Height;
        var quartzPoint = new CGPoint(point.X, primaryScreenHeight - point.Y);

        OnMove(quartzPoint);
        SetToolTipWindowPosition(quartzPoint);
    }

    private void SetToolTipWindowPosition(CGPoint pointerPoint)
    {
        const int margin = 16;

        // Use NSScreen to check bounds as we are working with logical points (CGPoint)
        var screen = NSScreen.Screens.AsValueEnumerable().FirstOrDefault(s =>
        {
            // NSScreen Frame is Cocoa (Bottom-Left). We need to convert our Quartz point back to check?
            // Or convert NSScreen Frame to Quartz.
            // Let's convert NSScreen Frame to Quartz for checking.
            var primaryHeight = NSScreen.Screens[0].Frame.Height;
            var quartzRect = new CGRect(
                s.Frame.X,
                primaryHeight - (s.Frame.Y + s.Frame.Height),
                s.Frame.Width,
                s.Frame.Height);
            return quartzRect.Contains(pointerPoint);
        });

        if (screen == null) return;

        // We need the bounds in Quartz coordinates for positioning logic
        var primaryScreenHeight = NSScreen.Screens[0].Frame.Height;
        var screenBounds = new CGRect(
            screen.Frame.X,
            primaryScreenHeight - (screen.Frame.Y + screen.Frame.Height),
            screen.Frame.Width,
            screen.Frame.Height);

        var tooltipSize = ToolTipWindow.Bounds.Size;

        var x = pointerPoint.X.Value;
        var y = pointerPoint.Y.Value - margin - tooltipSize.Height;

        // Check if there is enough space above the pointer
        if (y < screenBounds.Top) // Top is min Y in Quartz
        {
            y = pointerPoint.Y.Value + margin; // place below the pointer
        }

        // Check if there is enough space to the right of the pointer
        if (x + tooltipSize.Width > screenBounds.Right)
        {
            x = pointerPoint.X.Value - tooltipSize.Width; // place to the left of the pointer
        }

        ToolTipWindow.Position = new PixelPoint((int)x, (int)y);
    }

    protected virtual void OnCanceled()
    {
        SelectedElement = null;
    }

    protected virtual void OnMove(CGPoint point)
    {
        var maskRect = new PixelRect();
        switch (CurrentMode)
        {
            case ScreenSelectionMode.Screen:
            {
                var primaryHeight = NSScreen.Screens[0].Frame.Height;
                var screen = NSScreen.Screens.FirstOrDefault(s =>
                {
                    var quartzRect = new CGRect(
                        s.Frame.X,
                        primaryHeight - (s.Frame.Y + s.Frame.Height),
                        s.Frame.Width,
                        s.Frame.Height);
                    return quartzRect.Contains(point);
                });

                if (screen != null)
                {
                    // Temporary element just for rect
                    var el = new NSScreenVisualElement(screen);
                    SelectedElement = el; // used for screenshot capture later
                    maskRect = el.BoundingRectangle;
                }
                break;
            }
            case ScreenSelectionMode.Window:
            {
                SelectedElement = GetElementAtPoint();
                while (SelectedElement is AXUIElement axui && axui.Role != AXRoleAttribute.AXWindow)
                {
                    SelectedElement = SelectedElement.Parent;
                }
                if (SelectedElement != null) maskRect = SelectedElement.BoundingRectangle;
                break;
            }
            case ScreenSelectionMode.Element:
            {
                SelectedElement = GetElementAtPoint();
                if (SelectedElement != null) maskRect = SelectedElement.BoundingRectangle;
                break;
            }
        }

        foreach (var maskWindow in MaskWindows) maskWindow.SetMask(maskRect);
        ToolTipWindow.ToolTip.Element = SelectedElement;
        UpdateToolTipInfo(maskRect);

        AXUIElement? GetElementAtPoint()
        {
            _windowOwnerPids.Clear();
            GetWindowOwnerPidsAtLocation(point, _windowNumber, _windowOwnerPids);
            foreach (var windowOwnerPid in _windowOwnerPids)
            {
                if (AXUIElement.ElementFromPid(windowOwnerPid) is not { } element) continue;

                if (element.ElementAtPosition((float)point.X, (float)point.Y) is { } foundElement)
                {
                    return foundElement;
                }
            }

            return null;
        }
    }

    protected virtual void OnLeftButtonDown() { }

    /// <summary>
    /// Called when left mouse button is released.
    /// </summary>
    /// <returns>if true, the picking session will end and the window will close.</returns>
    protected virtual bool OnLeftButtonUp() => true;

    protected void UpdateToolTipInfo(PixelRect rect)
    {
        ToolTipWindow.ToolTip.SizeInfo = $"{rect.Width} x {rect.Height}";
    }

    private static void GetWindowOwnerPidsAtLocation(CGPoint point, uint relativeToWindow, List<int> pids)
    {
        var currentPid = Environment.ProcessId;
        var pArray = CGInterop.CGWindowListCopyWindowInfo(CGWindowListOption.OnScreenBelowWindow, relativeToWindow);
        if (pArray == 0) return;

        try
        {
            var array = Runtime.GetNSObject<NSArray>(pArray);
            if (array == null) return;

            using var kOwnerPid = new NSString("kCGWindowOwnerPID");
            using var kBounds = new NSString("kCGWindowBounds");
            using var kX = new NSString("X");
            using var kY = new NSString("Y");
            using var kW = new NSString("Width");
            using var kH = new NSString("Height");

            for (nuint i = 0; i < array.Count; i++)
            {
                var dict = array.GetItem<NSDictionary>(i);
                if (dict == null) continue;

                if (!dict.TryGetValue(kOwnerPid, out var pidObj) || pidObj is not NSNumber pidNum) continue;

                // Filter out our own process (Picker, Mask, ToolTip, etc.)
                if (pidNum.Int32Value == currentPid) continue;

                if (!dict.TryGetValue(kBounds, out var boundsObj) || boundsObj is not NSDictionary boundsDict) continue;
                if (!boundsDict.TryGetValue(kX, out var xObj) || xObj is not NSNumber x ||
                    !boundsDict.TryGetValue(kY, out var yObj) || yObj is not NSNumber y ||
                    !boundsDict.TryGetValue(kW, out var wObj) || wObj is not NSNumber w ||
                    !boundsDict.TryGetValue(kH, out var hObj) || hObj is not NSNumber h) continue;

                var rect = new CGRect(x.DoubleValue, y.DoubleValue, w.DoubleValue, h.DoubleValue);
                if (rect.Contains(point))
                {
                    pids.Add(pidNum.Int32Value);
                }
            }
        }
        finally
        {
            CFInterop.CFRelease(pArray);
        }
    }
}