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
            .ObserveOnDispatcher()
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
        var allPages = _serviceProvider
            .GetServices<IMainViewNavigationItemFactory>()
            .AsValueEnumerable()
            .SelectMany(f => f.CreateItems())
            .Concat(_serviceProvider.GetServices<IMainViewNavigationItem>())
            .ToList();
        var topLevelPages = allPages.AsValueEnumerable().OfType<IMainViewNavigationTopLevelItem>();
        var subPagesGroups = allPages.AsValueEnumerable().OfType<IMainViewNavigationSubItem>().GroupBy(p => p.GroupType);
        var rootItems = new List<(int Index, NavigationBarItem Item)>();

        foreach (var page in topLevelPages)
        {
            rootItems.Add((page.Index, new NavigationBarItem
            {
                Icon = page.Icon,
                [!ContentControl.ContentProperty] = page.TitleKey.ToBinding(),
                [!NavigationBarItem.ToolTipProperty] = page.TitleKey.ToBinding(),
                Route = page,
            }));
        }

        SettingsCategoryPage? categoryPage = null;
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
                };
                rootItems.Add((topLevelItem.Index, groupItem));
            }
            else
            {
                groupItem.Route = categoryPage = new SettingsCategoryPage
                {
                    [!SettingsCategoryPage.TitleProperty] = groupItem[!ContentControl.ContentProperty],
                    Command = new RelayCommand<NavigationBarItem>(i => SelectedItem = i)
                };
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
                categoryPage?.Items.Add(new SettingsCategoryPage.Item(subPage.Icon, subPage.TitleKey, subPage.DescriptionKey, item));
            }
        }

        _itemsSource.AddRange(rootItems.OrderBy(x => x.Index).Select(x => x.Item));

        SelectedItem = FindFirstRoutableItem(_itemsSource.Items);

        NavigationBarItem? FindFirstRoutableItem(IEnumerable<NavigationBarItem> items)
        {
            foreach (var item in items)
            {
                if (item.Route != null) return item;
                if (item.Children.Count <= 0) continue;
                var childItem = FindFirstRoutableItem(item.Children);
                if (childItem != null) return childItem;
            }
            return null;
        }
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
            DialogManager
                .CreateCustomDialog(_serviceProvider.GetRequiredService<ChangeLogView>())
                .Dismissible()
                .ShowAsync();
        }

        PersistentState.PreviousLaunchVersion = version?.ToString();
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