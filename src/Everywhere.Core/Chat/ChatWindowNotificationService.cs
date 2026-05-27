using System.Collections.ObjectModel;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.Controls.Notifications;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using DynamicData;
using DynamicData.Binding;
using Everywhere.AI;
using Everywhere.AI.Configurator;
using Everywhere.Cloud;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Messages;

namespace Everywhere.Chat;

public sealed class ChatWindowNotificationService : IChatWindowNotificationService, IDisposable
{
    private const string NotificationScope = "ChatWindow.OfficialModel";

    public ReadOnlyObservableCollection<DynamicNotification> Notifications => _notificationManager.Notifications;

    private readonly Settings _settings;
    private readonly IOfficialModelProvider _officialModelProvider;
    private readonly DynamicNotificationManager _notificationManager;
    private readonly CompositeDisposable _disposables = new();

    public ChatWindowNotificationService(
        Settings settings,
        IKeyValueStorage keyValueStorage,
        IOfficialModelProvider officialModelProvider)
    {
        _settings = settings;
        _officialModelProvider = officialModelProvider;
        _notificationManager = new DynamicNotificationManager(keyValueStorage, NotificationScope);
        _disposables.Add(_notificationManager);

        var selectedAssistantChanges = _settings.Model
            .WhenValueChanged(static x => x.SelectedCustomAssistant)
            .Select(static _ => 0);

        var assistantChanges = _settings.Model.CustomAssistants
            .ToObservableChangeSet()
            .AutoRefresh(static x => x.ConfiguratorType)
            .AutoRefresh(static x => x.ModelId)
            .AutoRefresh(static x => x.DeprecationDate)
            .ToCollection()
            .Select(static _ => 0);

        var officialModelDefinitionChanges = _officialModelProvider.ModelDefinitions
            .Connect()
            .ToCollection()
            .Select(static _ => 0);

        Observable
            .Merge(selectedAssistantChanges, assistantChanges, officialModelDefinitionChanges)
            .ObserveOnAvaloniaDispatcher()
            .Subscribe(_ => UpdateOfficialModelNotifications())
            .AddTo(_disposables);
    }

    private void UpdateOfficialModelNotifications()
    {
        var assistant = _settings.Model.SelectedCustomAssistant;
        if (assistant is null || assistant.ConfiguratorType != AssistantConfiguratorType.Official)
        {
            _notificationManager.Clear();
            return;
        }

        var today = DateOnly.FromDateTime(DateTime.Now);
        var availability = ModelAvailability.Evaluate(
            assistant,
            _officialModelProvider.ModelDefinitions.Items,
            today);
        if (!availability.ShouldShowChatNotification)
        {
            _notificationManager.Clear();
            return;
        }

        var dismissalKey = availability.CreateDismissalKey(assistant.Id, today);
        _notificationManager.Reset(
            new DynamicNotificationDescriptor(
                dismissalKey,
                CreateOfficialModelWarningMessageKey(availability),
                availability.Kind is ModelAvailabilityKind.Deprecated or ModelAvailabilityKind.Unavailable ?
                    NotificationType.Error :
                    NotificationType.Warning,
                ActionButtonContentKey: new DynamicResourceKey(LocaleKey.ChatWindow_ModelWarning_OpenAssistantSettings),
                ActionCommand: new RelayCommand(() => OpenAssistantSettings(assistant.Id))));
    }

    private static IDynamicResourceKey CreateOfficialModelWarningMessageKey(ModelAvailability availability)
    {
        var deprecationDate = new DirectResourceKey(availability.DeprecationDate?.ToString("D") ?? string.Empty);
        return availability.Kind switch
        {
            ModelAvailabilityKind.Unavailable => new FormattedDynamicResourceKey(
                LocaleKey.ChatWindow_ModelWarning_Unavailable),
            ModelAvailabilityKind.Deprecated => new FormattedDynamicResourceKey(
                LocaleKey.ChatWindow_ModelWarning_Deprecated,
                deprecationDate),
            ModelAvailabilityKind.DeprecatingSoon => new FormattedDynamicResourceKey(
                LocaleKey.ChatWindow_ModelWarning_DeprecatingSoon,
                deprecationDate),
            _ => DirectResourceKey.Empty
        };
    }

    private static void OpenAssistantSettings(Guid assistantId)
    {
        WeakReferenceMessenger.Default.Send<ApplicationMessage>(
            new ShowWindowMessage(ShowWindowMessage.MainWindow, "CustomAssistantPage"));
        WeakReferenceMessenger.Default.Send(new SelectCustomAssistantMessage(assistantId)); // TODO
    }

    public void Dispose()
    {
        _disposables.Dispose();
    }
}
