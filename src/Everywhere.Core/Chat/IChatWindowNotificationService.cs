using System.Collections.ObjectModel;
using Everywhere.Common;

namespace Everywhere.Chat;

public interface IChatWindowNotificationService
{
    ReadOnlyObservableCollection<DynamicNotification> Notifications { get; }
}
