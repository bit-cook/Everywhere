using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using CoreFoundation;
using Everywhere.Interop;
using ImageIO;
using ObjCRuntime;

namespace Everywhere.Mac.Interop;

/// <summary>
/// Provides interop methods for macOS Accessibility API (AXUIElement).
/// </summary>
public partial class AXUIElement : NSObject, IVisualElement
{
    public string Id => $"{ProcessId}.{NativeWindowHandle}.{CoreFoundationInterop.CFHash(Handle)}";

    public IVisualElement? Parent => field ??= GetAttributeAsElement(AXAttributeConstants.Parent);

    public VisualElementSiblingAccessor SiblingAccessor => new SiblingAccessorImpl(this);

    public IEnumerable<IVisualElement> Children
    {
        get
        {
            var children = GetAttribute<NSArray>(AXAttributeConstants.Children);
            if (children is null) yield break;

            for (nuint i = 0; i < children.Count; i++)
            {
                if (children.GetItem<AXUIElement>(i) is { } child)
                {
                    yield return child;
                }
            }
        }
    }

    public AXRoleAttribute Role { get; }

    public AXSubroleAttribute Subrole { get; }

    public VisualElementType Type
    {
        get
        {
            return Role switch
            {
                AXRoleAttribute.AXStaticText => VisualElementType.Label,
                AXRoleAttribute.AXTextField or
                    AXRoleAttribute.AXTextArea => VisualElementType.TextEdit,

                AXRoleAttribute.AXButton or
                    AXRoleAttribute.AXMenuButton or
                    AXRoleAttribute.AXPopUpButton or
                    AXRoleAttribute.AXDisclosureTriangle => VisualElementType.Button,

                AXRoleAttribute.AXCheckBox => VisualElementType.CheckBox,
                AXRoleAttribute.AXRadioButton => VisualElementType.RadioButton,
                AXRoleAttribute.AXComboBox => VisualElementType.ComboBox,

                AXRoleAttribute.AXList or
                    AXRoleAttribute.AXRuler => VisualElementType.ListView,

                AXRoleAttribute.AXOutline => VisualElementType.TreeView,
                AXRoleAttribute.AXTable => VisualElementType.Table,
                AXRoleAttribute.AXRow => VisualElementType.TableRow,

                AXRoleAttribute.AXMenuBar or
                    AXRoleAttribute.AXMenu => VisualElementType.Menu,

                AXRoleAttribute.AXMenuBarItem or
                    AXRoleAttribute.AXMenuItem => VisualElementType.MenuItem,

                AXRoleAttribute.AXTabGroup => VisualElementType.TabControl,
                AXRoleAttribute.AXToolbar => VisualElementType.ToolBar,

                AXRoleAttribute.AXGroup or
                    AXRoleAttribute.AXRadioGroup or
                    AXRoleAttribute.AXSplitGroup or
                    AXRoleAttribute.AXBrowser or
                    AXRoleAttribute.AXWindow or // Parent of AXWindow is AXApplication
                    AXRoleAttribute.AXSheet or
                    AXRoleAttribute.AXDrawer or
                    AXRoleAttribute.AXCell => VisualElementType.Panel,

                AXRoleAttribute.AXApplication or
                    AXRoleAttribute.AXSystemWide => VisualElementType.TopLevel,

                AXRoleAttribute.AXSplitter => VisualElementType.Splitter,
                AXRoleAttribute.AXSlider => VisualElementType.Slider,
                AXRoleAttribute.AXScrollBar => VisualElementType.ScrollBar,

                AXRoleAttribute.AXBusyIndicator => VisualElementType.Spinner,
                AXRoleAttribute.AXProgressIndicator or
                    AXRoleAttribute.AXLevelIndicator or
                    AXRoleAttribute.AXRelevanceIndicator or
                    AXRoleAttribute.AXValueIndicator => VisualElementType.ProgressBar,

                AXRoleAttribute.AXImage => VisualElementType.Image,
                AXRoleAttribute.AXLink => VisualElementType.Hyperlink,
                AXRoleAttribute.AXWebArea => VisualElementType.Document,

                AXRoleAttribute.AXScrollArea or
                    AXRoleAttribute.AXLayoutArea or
                    AXRoleAttribute.AXLayoutItem or
                    AXRoleAttribute.AXGrowArea or
                    AXRoleAttribute.AXMatte or
                    AXRoleAttribute.AXRulerMarker or
                    AXRoleAttribute.AXColumn or
                    AXRoleAttribute.AXGrid or
                    AXRoleAttribute.AXPage or
                    AXRoleAttribute.AXPopover => VisualElementType.Panel,

                _ => Subrole switch
                {
                    AXSubroleAttribute.AXCloseButton or
                        AXSubroleAttribute.AXMinimizeButton or
                        AXSubroleAttribute.AXZoomButton or
                        AXSubroleAttribute.AXToolbarButton or
                        AXSubroleAttribute.AXSortButton or
                        AXSubroleAttribute.AXTabButton => VisualElementType.Button,

                    AXSubroleAttribute.AXSearchField => VisualElementType.TextEdit,

                    AXSubroleAttribute.AXToggle or
                        AXSubroleAttribute.AXSwitch => VisualElementType.CheckBox,

                    AXSubroleAttribute.AXStandardWindow or
                        AXSubroleAttribute.AXDialog or
                        AXSubroleAttribute.AXSystemDialog or
                        AXSubroleAttribute.AXFloatingWindow or
                        AXSubroleAttribute.AXSystemFloatingWindow => VisualElementType.Panel,

                    _ => VisualElementType.Unknown
                }
            };
        }
    }

    public VisualElementStates States
    {
        get
        {
            var states = VisualElementStates.None;
            if (GetAttribute<NSNumber>(AXAttributeConstants.Enabled)?.BoolValue == false) states |= VisualElementStates.Disabled;
            if (GetAttribute<NSNumber>(AXAttributeConstants.Focused)?.BoolValue == true) states |= VisualElementStates.Focused;
            if (GetAttribute<NSNumber>(AXAttributeConstants.Hidden)?.BoolValue == true) states |= VisualElementStates.Offscreen;
            if (GetAttribute<NSNumber>(AXAttributeConstants.Selected)?.BoolValue == true) states |= VisualElementStates.Selected;

            if (Subrole == AXSubroleAttribute.AXSecureTextField) states |= VisualElementStates.Password;

            return states;
        }
    }

    public string? Name => GetAttribute<NSString>(AXAttributeConstants.Title);

    public PixelRect BoundingRectangle
    {
        get
        {
            try
            {
                var posVal = GetAttribute<AXValue>(AXAttributeConstants.Position);
                var sizeVal = GetAttribute<AXValue>(AXAttributeConstants.Size);

                if (posVal is null || sizeVal is null) return default;

                var pos = posVal.Point;
                var size = sizeVal.Size;
                return new PixelRect((int)pos.X, (int)pos.Y, (int)size.Width, (int)size.Height);
            }
            catch
            {
                return default;
            }
        }
    }

    public int ProcessId => GetPid(Handle, out var pid) == AXError.Success ? pid : 0;

    public nint NativeWindowHandle => GetWindow(Handle, out var windowId) == AXError.Success ? (nint)windowId : 0;

    static AXUIElement()
    {
        // default timeout for AX calls is 6s, which is too long for our use case.
        AXUIElementSetMessagingTimeout(SystemWide.Handle.Handle, 1f);
    }

    private AXUIElement(NativeHandle handle) : base(handle, true)
    {
        var axRole = GetAttribute<NSString>(AXAttributeConstants.Role);
        Role = Enum.TryParse<AXRoleAttribute>(axRole, true, out var role) ? role : AXRoleAttribute.AXUnknown;
        var axSubrole = GetAttribute<NSString>(AXAttributeConstants.Subrole);
        Subrole = Enum.TryParse<AXSubroleAttribute>(axSubrole, true, out var subrole) ? subrole : AXSubroleAttribute.AXUnknown;
    }

    public string? GetText(int maxLength = -1)
    {
        var text = GetAttribute<NSObject>(AXAttributeConstants.Value)?.ToString();
        if (string.IsNullOrEmpty(text)) return null;

        if (Role == AXRoleAttribute.AXCheckBox) return text == "0" ? "false" : "true"; // don't apply trim

        return maxLength > 0 && text.Length > maxLength ? text[..maxLength] : text;
    }

    // TODO: Is press enough?
    public void Invoke() => PerformAction(AXAttributeConstants.Press);

    public void SetText(string text)
    {
        using var nsText = new NSString(text);
        SetAttributeValue(Handle, AXAttributeConstants.Value.Handle, nsText.Handle);
    }

    public void SendShortcut(KeyboardShortcut shortcut)
    {
        // This is complex on macOS. It usually involves using CoreGraphics CGEventCreateKeyboardEvent
        // to create and post keyboard events to the process that owns the element.
        throw new NotImplementedException();
    }

    /// <summary>
    /// Get the selected text of the visual element.
    /// In case of numeric input fields that return NSNumber, it will be converted to string.
    /// </summary>
    /// <returns></returns>
    public string? GetSelectionText() => GetAttribute<NSObject>(AXAttributeConstants.SelectedText)?.ToString();

    public Task<Bitmap> CaptureAsync(CancellationToken cancellationToken)
    {
        var bounds = BoundingRectangle;
        var rect = new CGRect(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        var windowId = NativeWindowHandle;

        // we use CGSHWCaptureWindowList because it can screenshot minimized windows, which CGWindowListCreateImage can't
        using var cgImage = SkyLightInterop.HardwareCaptureWindowList(
            [(uint)windowId],
            SkyLightInterop.CGSWindowCaptureOptions.IgnoreGlobalCLipShape |
            SkyLightInterop.CGSWindowCaptureOptions.NominalResolution |
            SkyLightInterop.CGSWindowCaptureOptions.FullSize);

        if (cgImage is null)
        {
            return Task.FromException<Bitmap>(new InvalidOperationException("Failed to capture screen image."));
        }

        using var data = new NSMutableData();
        using var dest = CGImageDestination.Create(data, "public.png", 1);

        if (dest is null)
        {
            return Task.FromException<Bitmap>(new InvalidOperationException("Failed to create image destination."));
        }

        if (!rect.IsEmpty)
        {
            var screen = NSScreen.Screens.FirstOrDefault(s => s.Frame.IntersectsWith(rect));
            var scale = screen?.BackingScaleFactor ?? 1.0;
            using var croppedImage = cgImage.WithImageInRect(
                new CGRect(
                    rect.X * scale,
                    rect.Y * scale,
                    rect.Width * scale,
                    rect.Height * scale));

            if (croppedImage is null)
            {
                return Task.FromException<Bitmap>(new InvalidOperationException("Failed to crop image."));
            }

            dest.AddImage(croppedImage);
            dest.Close();

            // after this, we can safely dispose data
            return Task.FromResult(new Bitmap(data.AsStream()));
        }

        dest.AddImage(cgImage);
        dest.Close();

        // after this, we can safely dispose data
        return Task.FromResult(new Bitmap(data.AsStream()));
    }

    public bool SetAttribute(NSString attributeName, NSObject value)
    {
        var error = SetAttributeValue(Handle, attributeName.Handle, value.Handle);
        return error == AXError.Success;
    }

    public override bool Equals(object? obj)
    {
        return obj is AXUIElement element && CFType.Equal(Handle, element.Handle);
    }

    public override int GetHashCode()
    {
        return CoreFoundationInterop.CFHash(Handle).GetHashCode();
    }

    #region Helpers

    private T? GetAttribute<T>(NSString attributeName) where T : NSObject
    {
        var error = CopyAttributeValue(Handle, attributeName.Handle, out var value);
        return error == AXError.Success ? Runtime.GetNSObject<T>(value) : null;
    }

    private AXUIElement? GetAttributeAsElement(NSString attributeName)
    {
        var error = CopyAttributeValue(Handle, attributeName.Handle, out var value);
        if (error == AXError.Success && value != 0)
        {
            return new AXUIElement(value);
        }

        return null;
    }

    private void PerformAction(NSString actionName)
    {
        var error = PerformAction(Handle, actionName.Handle);
        if (error != AXError.Success)
        {
            throw new InvalidOperationException($"Failed to perform action {actionName}. Error: {error}");
        }
    }

    private const string AppServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";

    public static AXUIElement SystemWide { get; } = new(CreateSystemWide());

    public AXUIElement? ElementAtPosition(float x, float y)
    {
        var error = CopyElementAtPosition(Handle, x, y, out var element);
        return error == AXError.Success && element != 0 ? new AXUIElement(element) : null;
    }

    public AXUIElement? ElementByAttributeValue(NSString attributeName)
    {
        var error = CopyAttributeValue(Handle, attributeName.Handle, out var value);
        return error == AXError.Success && value != 0 ? new AXUIElement(value) : null;
    }

    public static AXUIElement? ElementFromPid(int pid)
    {
        var handle = CreateApplication(pid);
        return handle != 0 ? new AXUIElement(handle) : null;
    }

    [LibraryImport(AppServices, EntryPoint = "AXUIElementCreateSystemWide")]
    private static partial nint CreateSystemWide();

    [LibraryImport(AppServices, EntryPoint = "AXUIElementSetMessagingTimeout")]
    private static partial AXError AXUIElementSetMessagingTimeout(nint element, float timeoutInSeconds);

    [LibraryImport(AppServices, EntryPoint = "AXUIElementCopyElementAtPosition")]
    private static partial AXError CopyElementAtPosition(nint application, float x, float y, out nint element);

    [LibraryImport(AppServices, EntryPoint = "AXUIElementCopyAttributeValue")]
    private static partial AXError CopyAttributeValue(nint element, nint attribute, out nint value);

    [LibraryImport(AppServices, EntryPoint = "AXUIElementPerformAction")]
    private static partial AXError PerformAction(nint element, nint action);

    [LibraryImport(AppServices, EntryPoint = "AXUIElementSetAttributeValue")]
    private static partial AXError SetAttributeValue(nint element, nint attribute, nint value);

    /// <summary>
    /// Private API from https://github.com/lwouis/alt-tab-macos/blob/9761bb91e97646f1c30b43842c4694615e9ad39b/src/api-wrappers/private-apis/ApplicationServices.HIServices.framework.swift#L5
    /// </summary>
    [LibraryImport(AppServices, EntryPoint = "_AXUIElementGetWindow")]
    private static partial AXError GetWindow(nint element, out uint cgWindowId);

    [LibraryImport(AppServices, EntryPoint = "AXUIElementGetPid")]
    private static partial AXError GetPid(nint element, out int pid);

    [LibraryImport(AppServices, EntryPoint = "AXUIElementCreateApplication")]
    private static partial nint CreateApplication(int pid);

    #endregion

    private class SiblingAccessorImpl(AXUIElement origin) : VisualElementSiblingAccessor
    {
        private NSArray? _siblings;
        private nint _index;

        protected override void EnsureResources()
        {
            if (_siblings is not null) return;
            if (origin.Parent is not AXUIElement parent) return;

            _siblings = parent.GetAttribute<NSArray>(AXAttributeConstants.Children);
            _index = _siblings is not null ? (nint)_siblings.IndexOf(origin) : nint.MaxValue;
        }

        protected override void ReleaseResources()
        {
            if (_siblings is null) return;

            _siblings.Dispose();
            _siblings = null;
        }

        protected override IEnumerator<IVisualElement> CreateForwardEnumerator()
        {
            if (_siblings is not { } siblings || _index == nint.MaxValue) yield break;

            var count = (nint)siblings.Count;
            for (var i = _index + 1; i < count; i++)
            {
                if (siblings.GetItem<AXUIElement>((nuint)i) is { } sibling)
                {
                    yield return sibling;
                }
            }
        }

        protected override IEnumerator<IVisualElement> CreateBackwardEnumerator()
        {
            if (_siblings is not { } siblings || _index == nint.MaxValue) yield break;

            for (var i = _index - 1; i >= 0; i--)
            {
                if (siblings.GetItem<AXUIElement>((nuint)i) is { } sibling)
                {
                    yield return sibling;
                }
            }
        }
    }
}