using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Controls.Notifications;
using CommunityToolkit.Mvvm.Input;
using DynamicData;
using Everywhere.Configuration;

namespace Everywhere.Common;

public readonly record struct DynamicNotificationDescriptor(
    string Id,
    IDynamicResourceKey ContentKey,
    NotificationType Type = NotificationType.Information,
    bool CanDismiss = true,
    bool ForceShow = false,
    IDynamicResourceKey? ActionButtonContentKey = null,
    ICommand? ActionCommand = null);

public sealed class DynamicNotificationManager : IDisposable
{
    public ReadOnlyObservableCollection<DynamicNotification> Notifications { get; }

    private readonly IKeyValueStorage _keyValueStorage;
    private readonly string? _scope;
    private readonly SourceCache<DynamicNotification, string> _notificationsSourceList = new(n => n.Id);
    private readonly IDisposable _notificationsSourceSubscription;

    public DynamicNotificationManager(IKeyValueStorage keyValueStorage, string? scope = null)
    {
        _keyValueStorage = keyValueStorage;
        _scope = scope;
        _notificationsSourceSubscription = _notificationsSourceList.Connect().ObserveOnAvaloniaDispatcher().Bind(out var notifications).Subscribe();
        Notifications = notifications;
    }

    /// <summary>
    /// Pushes a new notification to be displayed. The notification will be automatically dismissed after a certain period, or can be dismissed manually if <paramref name="canDismiss"/> is true.
    /// If the notification with the same ID already exists, it will be replaced with the new one. This allows for updating existing notifications without stacking duplicates.
    /// Dismissed notifications will be automatically removed from the list, IKeyValueStorage can be used to persist dismissed notification IDs to prevent them from showing again in the future.
    /// </summary>
    /// <param name="id"></param>
    /// <param name="contentKey"></param>
    /// <param name="notificationType"></param>
    /// <param name="canDismiss"></param>
    /// <param name="forceShow">If true, the notification will be shown even if it has been marked as dismissed in the storage. This is useful for critical notifications that should always be shown at least once.</param>
    public void Push(
        string id,
        IDynamicResourceKey contentKey,
        NotificationType notificationType,
        bool canDismiss,
        bool forceShow = false) => Push(new DynamicNotificationDescriptor(id, contentKey, notificationType, canDismiss, forceShow));

    public void Push(DynamicNotificationDescriptor dynamicNotification)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dynamicNotification.Id);
        ArgumentNullException.ThrowIfNull(dynamicNotification.ContentKey);

        var key = CreateStorageKey(dynamicNotification.Id, _scope);
        if (dynamicNotification is { CanDismiss: true, ForceShow: false } && _keyValueStorage.Contains(key)) return;

        _notificationsSourceList.AddOrUpdate(CreateNotification(key, dynamicNotification));
    }

    /// <summary>
    /// Clears all notifications from the manager.
    /// This will remove all notifications from the display, but does not affect the dismissed state of any notifications in the storage.
    /// Use with caution, as this may lead to notifications being shown again if they are not marked as dismissed in the storage.
    /// </summary>
    public void Clear()
    {
        _notificationsSourceList.Clear();
    }

    public void Reset(params IEnumerable<DynamicNotificationDescriptor> notifications)
    {
        _notificationsSourceList.Edit(updater =>
        {
            updater.Clear();
            foreach (var notification in notifications)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(notification.Id);
                ArgumentNullException.ThrowIfNull(notification.ContentKey);

                var key = CreateStorageKey(notification.Id, _scope);
                if (notification is { CanDismiss: true, ForceShow: false } && _keyValueStorage.Contains(key)) continue;

                updater.AddOrUpdate(CreateNotification(key, notification));
            }
        });
    }

    internal static string CreateStorageKey(string id, string? scope) =>
        scope.IsNullOrEmpty() ? $"Notification:{id}" : $"Notification:{scope}:{id}";

    private DynamicNotification CreateNotification(string key, DynamicNotificationDescriptor notification) => new(
        key,
        notification.ContentKey,
        notification.Type,
        notification.CanDismiss ? new RelayCommand<DynamicNotification>(DismissNotification) : null,
        notification.ActionButtonContentKey,
        notification.ActionCommand);

    private void DismissNotification(DynamicNotification? notification)
    {
        if (notification is { CanDismiss: true })
        {
            // Mark the notification as dismissed in the storage to prevent it from showing again in the future.
            _keyValueStorage.Set(notification.Id, true);
            _notificationsSourceList.Remove(notification.Id);
        }
    }

    public void Dispose()
    {
        _notificationsSourceList.Dispose();
        _notificationsSourceSubscription.Dispose();
    }
}
