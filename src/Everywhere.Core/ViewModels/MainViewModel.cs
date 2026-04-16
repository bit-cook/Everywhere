using System.Collections.ObjectModel;
using System.Reactive.Disposables;
using System.Reflection;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DynamicData;
using Everywhere.Cloud;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Interop;
using Everywhere.Views;
using Microsoft.Extensions.DependencyInjection;
using ShadUI;
using ZLinq;

namespace Everywhere.ViewModels;

public sealed partial class MainViewModel : ReactiveViewModelBase, IDisposable
{
    [ObservableProperty] public partial NavigationBarItem? SelectedItem { get; set; }

    public ReadOnlyObservableCollection<NavigationBarItem> Items { get; }

    public ICloudClient CloudClient { get; }

    /// <summary>
    /// Use public property for MVVM binding
    /// </summary>
    public PersistentState PersistentState { get; }

    private readonly SourceList<NavigationBarItem> _itemsSource = new();
    private readonly CompositeDisposable _disposables = new(2);

    private readonly IServiceProvider _serviceProvider;
    private readonly Settings _settings;

    public MainViewModel(
        ICloudClient cloudClient,
        IServiceProvider serviceProvider,
        Settings settings,
        PersistentState persistentState)
    {
        CloudClient = cloudClient;

        _serviceProvider = serviceProvider;
        _settings = settings;
        PersistentState = persistentState;

        Items = _itemsSource
            .Connect()
            .ObserveOnAvaloniaDispatcher()
            .BindEx(_disposables);
    }

    protected internal override async Task ViewLoaded(CancellationToken cancellationToken)
    {
        if (_itemsSource.Count > 0)
        {
            await base.ViewLoaded(cancellationToken);
            return;
        }

        InitializeNavigationBarItems();
        ShowOobeDialogOnDemand();

        await base.ViewLoaded(cancellationToken);
    }

    private void InitializeNavigationBarItems()
    {
        var allPages = _serviceProvider.GetServices<IMainViewNavigationItem>().AsValueEnumerable();
        var topLevelPages = allPages.OfType<IMainViewNavigationTopLevelItem>();
        var subPagesGroups = allPages.OfType<IMainViewNavigationSubItem>().GroupBy(p => p.GroupType);
        var rootItems = new List<(int Index, NavigationBarItem Item)>();

        foreach (var page in topLevelPages)
        {
            NavigationBarItem item;
            rootItems.Add(
                (page.Index, item = new NavigationBarItem
                {
                    Icon = page.Icon,
                    [!ContentControl.ContentProperty] = page.TitleKey.ToBinding(),
                    [!NavigationBarItem.ToolTipProperty] = page.TitleKey.ToBinding(),
                    Route = page,
                }));

            if (page is IMainViewNavigationTopLevelItemWithSubItems factory)
            {
                item.Children.AddRange(
                    factory.CreateSubItems().Select(i => new NavigationBarItem
                    {
                        Icon = i.Icon,
                        [!ContentControl.ContentProperty] = i.TitleKey.ToBinding(),
                        [!NavigationBarItem.ToolTipProperty] = i.TitleKey.ToBinding(),
                        Route = i
                    }));
            }
        }

        foreach (var group in subPagesGroups)
        {
            if (rootItems.AsValueEnumerable().FirstOrDefault(r => r.Item.Route?.GetType() == group.Key) is not (_, { } groupItem))
            {
                var topLevelItem = (IMainViewNavigationTopLevelItem)_serviceProvider.GetRequiredService(group.Key);
                groupItem = new NavigationBarItem
                {
                    Icon = topLevelItem.Icon,
                    [!ContentControl.ContentProperty] = topLevelItem.TitleKey.ToBinding(),
                    [!NavigationBarItem.ToolTipProperty] = topLevelItem.TitleKey.ToBinding(),
                    Route = topLevelItem
                };
                rootItems.Add((topLevelItem.Index, groupItem));
            }

            foreach (var subPage in group.AsValueEnumerable().OrderBy(p => p.Index))
            {
                var item = new NavigationBarItem
                {
                    [!ContentControl.ContentProperty] = subPage.TitleKey.ToBinding(),
                    [!NavigationBarItem.ToolTipProperty] = subPage.TitleKey.ToBinding(),
                    Route = subPage,
                };
                groupItem.Children.Add(item);
            }
        }

        _itemsSource.AddRange(rootItems.OrderBy(x => x.Index).Select(x => x.Item));

        SelectedItem = FindNavigationBarItem(_itemsSource.Items, i => i.Route is not null);
    }

    private static NavigationBarItem? FindNavigationBarItem(IEnumerable<NavigationBarItem> items, Predicate<NavigationBarItem> match)
    {
        foreach (var item in items)
        {
            if (match(item)) return item;
            if (item.Children.Count <= 0) continue;

            var child = FindNavigationBarItem(item.Children, match);
            if (child != null) return child;
        }

        return null;
    }

    /// <summary>
    /// Shows the OOBE dialog if the application is launched for the first time or after an update.
    /// </summary>
    private void ShowOobeDialogOnDemand()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        if (!Version.TryParse(PersistentState.PreviousLaunchVersion, out var previousLaunchVersion)) previousLaunchVersion = null;
        if (_settings.Model.CustomAssistants.Count == 0)
        {
            DialogManager
                .CreateCustomDialog(_serviceProvider.GetRequiredService<WelcomeView>())
                .ShowAsync();
        }
        else if (previousLaunchVersion != version)
        {
            NavigateTo(_serviceProvider.GetRequiredService<ChangeLogView>());
            ToastManager
                .CreateToast(LocaleResolver.MainViewModel_UpgradeSuccessfulToast_Title)
                .WithDurationSeconds(5)
                .ShowAsync();
        }

        PersistentState.PreviousLaunchVersion = version?.ToString();
    }

    [RelayCommand]
    private void NavigateToType(Type routeType)
    {
        var item = FindNavigationBarItem(
            _itemsSource.Items,
            i => i.Route?.GetType() == routeType || i.Children.Any(c => c.Route?.GetType() == routeType));
        if (item != null) SelectedItem = item;
    }

    [RelayCommand]
    public void NavigateTo(object route)
    {
        var item = FindNavigationBarItem(_itemsSource.Items, i => i.Route == route);
        SelectedItem = item ?? new NavigationBarItem(route); // This allows navigating to a route that is not in the navigation bar
    }

    protected internal override Task ViewUnloaded()
    {
        ShowHideToTrayNotificationOnDemand();

        return base.ViewUnloaded();
    }

    private void ShowHideToTrayNotificationOnDemand()
    {
        if (PersistentState.IsHideToTrayIconNotificationShown) return;

        ServiceLocator.Resolve<INativeHelper>().ShowDesktopNotificationAsync(LocaleResolver.MainView_EverywhereHasMinimizedToTray);
        PersistentState.IsHideToTrayIconNotificationShown = true;
    }

    public void Dispose()
    {
        _disposables.Dispose();
    }
}