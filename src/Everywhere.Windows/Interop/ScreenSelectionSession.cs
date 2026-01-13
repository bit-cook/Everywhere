using System.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;
using Avalonia;
using Avalonia.Input;
using Avalonia.Threading;
using DynamicData;
using Everywhere.Interop;
using Everywhere.Views;
using Point = System.Drawing.Point;

namespace Everywhere.Windows.Interop;

public partial class VisualElementContext
{
    private abstract class ScreenSelectionSession : ScreenSelectionTransparentWindow
    {
        protected IWindowHelper WindowHelper { get; }
        protected ScreenSelectionMaskWindow[] MaskWindows { get; }
        protected ScreenSelectionToolTipWindow ToolTipWindow { get; }

        protected ScreenSelectionMode CurrentMode { get; private set; }
        protected IVisualElement? SelectedElement { get; private set; }

        private readonly IReadOnlyList<ScreenSelectionMode> _allowedModes;
        private readonly PixelRect _allScreenBounds;

        private bool _isRightButtonPressed;
        private IDisposable? _mouseHookSubscription;
        private IDisposable? _keyboardHookSubscription;

        protected ScreenSelectionSession(IWindowHelper windowHelper, IReadOnlyList<ScreenSelectionMode> allowedModes, ScreenSelectionMode initialMode)
        {
            Debug.Assert(allowedModes.Count > 0);

            _allowedModes = allowedModes;
            WindowHelper = windowHelper;
            CurrentMode = initialMode;

            var allScreens = Screens.All;
            MaskWindows = new ScreenSelectionMaskWindow[allScreens.Count];
            _allScreenBounds = new PixelRect();
            for (var i = 0; i < allScreens.Count; i++)
            {
                var screen = allScreens[i];
                _allScreenBounds = _allScreenBounds.Union(screen.Bounds);
                var maskWindow = new ScreenSelectionMaskWindow(screen.Bounds);
                windowHelper.SetHitTestVisible(maskWindow, false);
                MaskWindows[i] = maskWindow;
            }

            // Cover the entire virtual screen
            SetPlacement(_allScreenBounds, out _);

            ToolTipWindow = new ScreenSelectionToolTipWindow(allowedModes, initialMode);
            windowHelper.SetHitTestVisible(ToolTipWindow, false);
        }

        protected override unsafe void OnPointerEntered(PointerEventArgs e)
        {
            // Simulate a mouse left button down in the top-left corner of the window (8,8 to avoid the border)
            var x = (_allScreenBounds.X + 8d) / _allScreenBounds.Width * 65535;
            var y = (_allScreenBounds.Y + 8d) / _allScreenBounds.Height * 65535;

            // SendInput MouseRightButtonDown, this will:
            // 1. prevent the cursor from changing to the default arrow cursor and interacting with other windows
            // 2. Trigger the OnPointerPressed event to set the window to hit test invisible
            PInvoke.SendInput(
                new ReadOnlySpan<INPUT>(
                [
                    new INPUT
                    {
                        type = INPUT_TYPE.INPUT_MOUSE,
                        Anonymous = new INPUT._Anonymous_e__Union
                        {
                            mi = new MOUSEINPUT
                            {
                                dwFlags = MOUSE_EVENT_FLAGS.MOUSEEVENTF_RIGHTDOWN | MOUSE_EVENT_FLAGS.MOUSEEVENTF_ABSOLUTE,
                                dx = (int)x,
                                dy = (int)y
                            }
                        }
                    },
                ]),
                sizeof(INPUT));
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            // This should be triggered by the SendInput above
            if (_isRightButtonPressed || !e.Properties.IsRightButtonPressed) return;

            _isRightButtonPressed = true;
            WindowHelper.SetHitTestVisible(this, false);
            foreach (var maskWindow in MaskWindows) maskWindow.Show(this);
            ToolTipWindow.Show(this);

            // Install a low-level mouse hook to listen for right button down events
            _mouseHookSubscription ??= LowLevelHook.CreateMouseHook((msg, ref hookStruct, ref blockNext) =>
            {
                switch (msg)
                {
                    // Close the window and cancel selection on right button up (Exit the picking mode)
                    case WINDOW_MESSAGE.WM_RBUTTONUP:
                    {
                        blockNext = true;
                        OnCanceled();
                        Dispatcher.UIThread.Post(Close, DispatcherPriority.Default);
                        break;
                    }
                    case WINDOW_MESSAGE.WM_LBUTTONUP:
                    {
                        blockNext = true;
                        if (OnLeftButtonUp())
                        {
                            Dispatcher.UIThread.Post(Close, DispatcherPriority.Default);
                        }
                        break;
                    }
                    case WINDOW_MESSAGE.WM_LBUTTONDOWN:
                    {
                        blockNext = true;
                        OnLeftButtonDown();
                        break;
                    }
                    // Use scroll wheel to change pick mode
                    case WINDOW_MESSAGE.WM_MOUSEWHEEL:
                    {
                        blockNext = true;
                        OnMouseWheel((int)hookStruct.mouseData >> 16);
                        break;
                    }
                    case WINDOW_MESSAGE.WM_MOUSEMOVE:
                    {
                        // Update drag if necessary
                        OnNativeMouseMove();
                        break;
                    }
                    default:
                    {
                        blockNext = true; // block all other mouse events
                        break;
                    }
                }
            });

            _keyboardHookSubscription ??= LowLevelHook.CreateKeyboardHook((msg, ref hookStruct, ref blockNext) =>
            {
                // Block all key events
                blockNext = true;

                var isKeyDown = msg is WINDOW_MESSAGE.WM_KEYDOWN or WINDOW_MESSAGE.WM_SYSKEYDOWN;
                if (!isKeyDown) return;

                OnKeyDown((VIRTUAL_KEY)hookStruct.vkCode);
            });

            // Pick the element under the cursor immediately
            HandlePointerMoved();
        }

        private void OnMouseWheel(int delta)
        {
            var newIndex = _allowedModes.IndexOf(CurrentMode) + (delta > 0 ? -1 : 1);
            newIndex = Math.Clamp(newIndex, 0, _allowedModes.Count - 1);
            CurrentMode = _allowedModes[newIndex];
            HandlePickModeChanged();
        }

        private void OnKeyDown(VIRTUAL_KEY key)
        {
            switch (key)
            {
                case VIRTUAL_KEY.VK_ESCAPE:
                {
                    OnCanceled();
                    Dispatcher.UIThread.Post(Close, DispatcherPriority.Default);
                    break;
                }
                case VIRTUAL_KEY.VK_NUMPAD1 or VIRTUAL_KEY.VK_1 or VIRTUAL_KEY.VK_F1:
                {
                    CurrentMode = ScreenSelectionMode.Screen;
                    HandlePickModeChanged();
                    break;
                }
                case VIRTUAL_KEY.VK_NUMPAD2 or VIRTUAL_KEY.VK_2 or VIRTUAL_KEY.VK_F2:
                {
                    CurrentMode = ScreenSelectionMode.Window;
                    HandlePickModeChanged();
                    break;
                }
                case VIRTUAL_KEY.VK_NUMPAD3 or VIRTUAL_KEY.VK_3 or VIRTUAL_KEY.VK_F3:
                {
                    CurrentMode = ScreenSelectionMode.Element;
                    HandlePickModeChanged();
                    break;
                }
                // Add shortcut for Free mode? F4?
                case VIRTUAL_KEY.VK_NUMPAD4 or VIRTUAL_KEY.VK_4 or VIRTUAL_KEY.VK_F4:
                {
                    CurrentMode = ScreenSelectionMode.Free;
                    HandlePickModeChanged();
                    break;
                }
            }
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            HandlePointerMoved();
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            Close();
        }

        protected override unsafe void OnClosed(EventArgs e)
        {
            _mouseHookSubscription?.Dispose();
            _keyboardHookSubscription?.Dispose();

            // right button down event (from SendInput) is not blocked and triggered OnPointerPressed
            // so currently system thinks right button is still pressed
            // we need to let the right button up event go through to avoid stuck right button state
            PInvoke.SendInput(
                new ReadOnlySpan<INPUT>(
                [
                    new INPUT
                    {
                        type = INPUT_TYPE.INPUT_MOUSE,
                        Anonymous = new INPUT._Anonymous_e__Union
                        {
                            mi = new MOUSEINPUT
                            {
                                dwFlags = MOUSE_EVENT_FLAGS.MOUSEEVENTF_RIGHTUP | MOUSE_EVENT_FLAGS.MOUSEEVENTF_ABSOLUTE,
                            }
                        }
                    },
                ]),
                sizeof(INPUT));
        }

        private void HandlePickModeChanged()
        {
            HandlePointerMoved();
            Dispatcher.UIThread.Post(
                () => ToolTipWindow.ToolTip.Mode = CurrentMode,
                DispatcherPriority.Background);
        }

        private void HandlePointerMoved()
        {
            if (!PInvoke.GetCursorPos(out var point)) return;

            OnMove(point);
            SetToolTipWindowPosition(new PixelPoint(point.X, point.Y));
        }

        private void OnNativeMouseMove()
        {
            HandlePointerMoved();
        }

        private void SetToolTipWindowPosition(PixelPoint pointerPoint)
        {
            const int margin = 16;

            var screen = Screens.All.FirstOrDefault(s => s.Bounds.Contains(pointerPoint));
            if (screen == null) return;

            var screenBounds = screen.Bounds;
            var tooltipSize = ToolTipWindow.Bounds.Size * ToolTipWindow.DesktopScaling;

            var x = (double)pointerPoint.X;
            var y = pointerPoint.Y - margin - tooltipSize.Height;

            // Check if there is enough space above the pointer
            if (y < 0d)
            {
                y = pointerPoint.Y + margin; // place below the pointer
            }

            // Check if there is enough space to the right of the pointer
            if (x + tooltipSize.Width > screenBounds.Right)
            {
                x = pointerPoint.X - tooltipSize.Width; // place to the left of the pointer
            }

            ToolTipWindow.Position = new PixelPoint((int)x, (int)y);
        }

        protected virtual void OnCanceled()
        {
            SelectedElement = null;
        }

        protected virtual void OnMove(Point point)
        {
            var maskRect = new PixelRect();
            var pixelPoint = new PixelPoint(point.X, point.Y);
            switch (CurrentMode)
            {
                case ScreenSelectionMode.Screen:
                {
                    var screen = Screens.All.FirstOrDefault(s => s.Bounds.Contains(pixelPoint));
                    if (screen == null) break;

                    var hMonitor = PInvoke.MonitorFromPoint(point, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
                    if (hMonitor == HMONITOR.Null) break;

                    SelectedElement = new ScreenVisualElementImpl(hMonitor);
                    maskRect = screen.Bounds;
                    break;
                }
                case ScreenSelectionMode.Window:
                {
                    var selectedHWnd = PInvoke.WindowFromPoint(point);
                    if (selectedHWnd == HWND.Null) break;

                    var rootHWnd = PInvoke.GetAncestor(selectedHWnd, GET_ANCESTOR_FLAGS.GA_ROOTOWNER);
                    if (rootHWnd == HWND.Null) break;

                    SelectedElement = TryCreateVisualElement(() => Automation.FromHandle(rootHWnd));
                    if (SelectedElement == null) break;

                    maskRect = SelectedElement.BoundingRectangle;
                    break;
                }
                case ScreenSelectionMode.Element:
                {
                    // BUG: sometimes this only picks the window, not the element under the cursor (e.g. QQ)
                    SelectedElement = TryCreateVisualElement(() => Automation.FromPoint(point));
                    if (SelectedElement == null) break;

                    maskRect = SelectedElement.BoundingRectangle;
                    break;
                }
            }

            foreach (var maskWindow in MaskWindows) maskWindow.SetMask(maskRect);
            ToolTipWindow.ToolTip.Element = SelectedElement;
        }

        /// <summary>
        /// Called when Left Button Down.
        /// </summary>
        protected virtual void OnLeftButtonDown() { }

        /// <summary>
        /// Called when Left Button Up.
        /// Returns true if the picker should close.
        /// </summary>
        protected virtual bool OnLeftButtonUp() => true;
    }
}