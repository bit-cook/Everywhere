using Everywhere.Collections;
using Everywhere.Common;

namespace Everywhere.Chat;

public interface IChatWindowNotificationService
{
    IReadOnlyBindableList<DynamicNotification> Notifications { get; }
}
