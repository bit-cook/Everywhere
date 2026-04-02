// resharper disable InconsistentNaming

using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Input.TextInput;
using Avalonia.Interactivity;
using AvaloniaEdit.Editing;
using HarmonyLib;

namespace Everywhere.Patches.Avalonia;

public sealed class PreeditChangedEventArgs(RoutedEvent routedEvent, string? preeditText, Rect cursorRectangle) : RoutedEventArgs(routedEvent)
{
    public string? PreeditText { get; } = preeditText;

    public Rect CursorRectangle { get; } = cursorRectangle;
}

public static class PreeditChangedEventRegistry
{
    public static readonly RoutedEvent<PreeditChangedEventArgs> PreeditChangedEvent =
        RoutedEvent.Register<TextArea, PreeditChangedEventArgs>("PreeditChanged", RoutingStrategies.Bubble);
}

internal static class TextAreaTextInputMethodClient_Preedit
{
    public static void Patch(Harmony harmony)
    {
        var type = typeof(TextArea).GetNestedType("TextAreaTextInputMethodClient", BindingFlags.NonPublic);
        harmony.Patch(AccessTools.PropertyGetter(type, nameof(TextInputMethodClient.SupportsPreedit)), new HarmonyMethod(SupportsPreedit));
        harmony.Patch(AccessTools.Method(type, nameof(TextInputMethodClient.SetPreeditText), [typeof(string)]), new HarmonyMethod(SetPreeditText));
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_textArea")]
    private static extern ref TextArea? GetTextArea(
        [UnsafeAccessorType("AvaloniaEdit.Editing.TextArea+TextAreaTextInputMethodClient, AvaloniaEdit")]
        object @this);

    private static bool SupportsPreedit(object __instance, ref bool __result)
    {
        __result = true;
        return false;
    }

    private static bool SetPreeditText(object __instance, ref string? text)
    {
        var textArea = GetTextArea(__instance);
        var client = (TextInputMethodClient)__instance;
        textArea?.RaiseEvent(new PreeditChangedEventArgs(PreeditChangedEventRegistry.PreeditChangedEvent, text, client.CursorRectangle));
        return false;
    }
}
// resharper restore InconsistentNaming