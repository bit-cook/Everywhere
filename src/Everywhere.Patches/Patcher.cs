using Everywhere.Patches.Avalonia;
using Everywhere.Patches.SemanticKernel;
using HarmonyLib;

namespace Everywhere.Patches;

public static class Patcher
{
    public static void PatchAll()
    {
        var harmony = new Harmony("com.sylinko.everywhere");
        TextAreaTextInputMethodClient_Preedit.Patch(harmony);
        TextLeadingPrefixCharacterEllipsis_Collapse.Patch(harmony);
        ChatResponseUpdateExtensions_ToStreamingChatMessageContent.Patch(harmony);
    }
}