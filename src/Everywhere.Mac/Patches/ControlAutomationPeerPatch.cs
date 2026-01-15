using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Automation.Peers;
using HarmonyLib;

namespace Everywhere.Mac.Patches;

/// <summary>
/// ControlAutomationPeer.CreatePeerForElement can be only called on UI Thread,
/// causing crashing when invoking ChatWindow on MainWindow.
/// We use Lib.Harmony to patch it
/// </summary>
[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
public class ControlAutomationPeerPatch
{
    public static void Patch(Harmony harmony)
    {
        var method = AccessTools.Method(typeof(ControlAutomationPeer), nameof(ControlAutomationPeer.CreatePeerForElement));
        harmony.Patch(method, new HarmonyMethod(typeof(ControlAutomationPeerPatch), nameof(PatchedMethod)));
    }

    /// <summary>
    /// Returns an empty automation peer to avoid UI thread requirement.
    /// </summary>
    /// <param name="__result"></param>
    /// <returns></returns>
    // ReSharper disable once InconsistentNaming
    // ReSharper disable once RedundantAssignment
    private static bool PatchedMethod(ref AutomationPeer __result)
    {
        __result = EmptyAutomationPeer.Shared;
        return false; // Skip original method
    }

    /// <summary>
    /// An automation peer which represents an element that is exposed to automation as non-
    /// interactive or as not contributing to the logical structure of the application.
    /// It does nothing with UI elements so no UI thread is required.
    /// </summary>
    private sealed class EmptyAutomationPeer : AutomationPeer
    {
        public static EmptyAutomationPeer Shared { get; } = new();

        protected override void BringIntoViewCore() { }

        protected override string? GetAcceleratorKeyCore() => null;

        protected override string? GetAccessKeyCore() => null;

        protected override AutomationControlType GetAutomationControlTypeCore() => default;

        protected override string? GetAutomationIdCore() => null;

        protected override Rect GetBoundingRectangleCore() => default;

        protected override IReadOnlyList<AutomationPeer> GetOrCreateChildrenCore() => [];

        protected override string GetClassNameCore() => string.Empty;

        protected override AutomationPeer? GetLabeledByCore() => null;

        protected override string? GetNameCore() => null;

        protected override AutomationPeer? GetParentCore() => null;

        protected override bool HasKeyboardFocusCore() => false;

        protected override bool IsContentElementCore() => false;

        protected override bool IsControlElementCore() => false;

        protected override bool IsEnabledCore() => false;

        protected override bool IsKeyboardFocusableCore() => false;

        protected override void SetFocusCore() { }

        protected override bool ShowContextMenuCore() => false;

        protected override bool TrySetParent(AutomationPeer? parent) => false;
    }
}