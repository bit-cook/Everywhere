using System.Windows.Input;
using Avalonia.Controls.Notifications;
using Everywhere.I18N;

namespace Everywhere.Common;

public sealed record DynamicNotification(
    string Id,
    IDynamicResourceKey ContentKey,
    NotificationType Type,
    ICommand? DismissCommand,
    IDynamicResourceKey? ActionButtonContentKey = null,
    ICommand? ActionCommand = null
)
{
    public bool CanDismiss => DismissCommand is not null && DismissCommand.CanExecute(this);
}
