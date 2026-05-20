using System.Windows.Input;
using ShadUI;

namespace Everywhere.Chat;

public sealed record ChatWindowNotification(
    string Key,
    IDynamicResourceKey MessageKey,
    Notification Severity,
    bool CanDismiss,
    ICommand? Command = null)
{
    public IDynamicResourceKey? ActionButtonContentKey =>
        Command is null ? null : new DynamicResourceKey(LocaleKey.ChatWindow_ModelWarning_OpenAssistantSettings);
}
