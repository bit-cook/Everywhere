using System.Collections.ObjectModel;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using Avalonia.Controls.Primitives;
using CommunityToolkit.Mvvm.Input;
using DynamicData;
using Everywhere.AI;
using Everywhere.Cloud;
using Everywhere.Common;
using Microsoft.Extensions.DependencyInjection;
using ShadUI;
using ZLinq;

namespace Everywhere.Views;

public partial class OfficialModelDefinitionForm : TemplatedControl
{
    private readonly SourceCache<ModelDefinitionTemplate, string> _uiProxyCache = new(x => x.ModelId);

    public static readonly DirectProperty<OfficialModelDefinitionForm, ReadOnlyObservableCollection<ModelDefinitionTemplate>?> ItemsSourceProperty =
        AvaloniaProperty.RegisterDirect<OfficialModelDefinitionForm, ReadOnlyObservableCollection<ModelDefinitionTemplate>?>(
            nameof(ItemsSource),
            o => o.ItemsSource);

    public ReadOnlyObservableCollection<ModelDefinitionTemplate> ItemsSource { get; }

    private ModelDefinitionTemplate? _selectedItem;
    public static readonly DirectProperty<OfficialModelDefinitionForm, ModelDefinitionTemplate?> SelectedItemProperty =
        AvaloniaProperty.RegisterDirect<OfficialModelDefinitionForm, ModelDefinitionTemplate?>(
            nameof(SelectedItem),
            o => o.SelectedItem,
            (o, v) => o.SelectedItem = v);

    public ModelDefinitionTemplate? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (!SetAndRaise(SelectedItemProperty, ref _selectedItem, value)) return;

            if (value is not null) _assistant.ApplyTemplate(value);
        }
    }

    public ICloudClient CloudClient { get; }

    public IOfficialModelProvider OfficialModelProvider { get; }

    private readonly IExceptionHandler _exceptionHandler;
    private readonly Assistant _assistant;

    private CompositeDisposable? _visualTreeDisposables;
    private bool _isFirstLoad = true;

    public OfficialModelDefinitionForm(IServiceProvider serviceProvider, Assistant assistant)
    {
        _assistant = assistant;
        CloudClient = serviceProvider.GetRequiredService<ICloudClient>();
        OfficialModelProvider = serviceProvider.GetRequiredService<IOfficialModelProvider>();
        _exceptionHandler = serviceProvider.GetRequiredKeyedService<IExceptionHandler>(typeof(ToastManager));

        _uiProxyCache
            .Connect()
            .ObserveOnAvaloniaDispatcher()
            .Bind(out var readOnlyList)
            .Subscribe();
        ItemsSource = readOnlyList;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _visualTreeDisposables = new CompositeDisposable();

        OfficialModelProvider.ModelDefinitions
            .Connect()
            .ToCollection()
            .ObserveOnAvaloniaDispatcher()
            .Subscribe(ReconcileProxyList)
            .DisposeWith(_visualTreeDisposables);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _visualTreeDisposables?.Dispose();
        _visualTreeDisposables = null;
    }

    private void ReconcileProxyList(IReadOnlyCollection<ModelDefinitionTemplate> cloudItems)
    {
        var finalItems = cloudItems.ToList();

        var targetModelId = _isFirstLoad ? _assistant.ModelId : _selectedItem?.ModelId;
        if (!string.IsNullOrEmpty(targetModelId))
        {
            if (finalItems.AsValueEnumerable().All(x => x.ModelId != targetModelId))
            {
                if (_assistant.ModelId is not null)
                {
                    finalItems.Insert(0, new ModelDefinitionTemplate
                    {
                        ModelId = _assistant.ModelId,
                        Name = _assistant.ModelId,
                        SupportsReasoning = _assistant.SupportsReasoning,
                        SupportsToolCall = _assistant.SupportsToolCall,
                        InputModalities = _assistant.InputModalities,
                        OutputModalities = _assistant.OutputModalities,
                        ContextLimit = _assistant.ContextLimit,
                        OutputLimit = _assistant.OutputLimit
                    });
                }
                else if (_selectedItem is not null)
                {
                    finalItems.Insert(0, _selectedItem);
                }
            }
        }

        _uiProxyCache.EditDiff(finalItems, (oldItem, newItem) => oldItem.ModelId == newItem.ModelId);

        if (!string.IsNullOrEmpty(targetModelId))
        {
            var proxyItem = _uiProxyCache.Lookup(targetModelId);
            if (proxyItem.HasValue)
            {
                SelectedItem = proxyItem.Value;
            }
        }

        if (_isFirstLoad) _isFirstLoad = false;
    }

    [RelayCommand]
    private Task RefreshAsync() => OfficialModelProvider.RefreshAsync(_exceptionHandler);
}