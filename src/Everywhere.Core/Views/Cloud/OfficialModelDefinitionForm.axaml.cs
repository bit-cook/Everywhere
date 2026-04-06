using System.Collections.ObjectModel;
using Avalonia.Controls.Primitives;
using CommunityToolkit.Mvvm.Input;
using Everywhere.AI;
using Everywhere.Cloud;
using Everywhere.Common;
using Microsoft.Extensions.DependencyInjection;
using ShadUI;

namespace Everywhere.Views;

public partial class OfficialModelDefinitionForm : TemplatedControl
{
    public static readonly DirectProperty<OfficialModelDefinitionForm, ReadOnlyObservableCollection<ModelDefinitionTemplate>> ItemsSourceProperty =
        AvaloniaProperty.RegisterDirect<OfficialModelDefinitionForm, ReadOnlyObservableCollection<ModelDefinitionTemplate>>(
            nameof(ItemsSource),
            o => o.ItemsSource);

    public ReadOnlyObservableCollection<ModelDefinitionTemplate> ItemsSource => _officialModelProvider.ModelDefinitions;

    public static readonly DirectProperty<OfficialModelDefinitionForm, ModelDefinitionTemplate?> SelectedItemProperty =
        AvaloniaProperty.RegisterDirect<OfficialModelDefinitionForm, ModelDefinitionTemplate?>(
        nameof(SelectedItem),
        o => o.SelectedItem,
        (o, v) => o.SelectedItem = v);

    public ModelDefinitionTemplate? SelectedItem
    {
        get;
        set
        {
            if (!SetAndRaise(SelectedItemProperty, ref field, value)) return;

            _assistant.ApplyTemplate(value);
        }
    }

    public ICloudClient CloudClient { get; }

    private readonly IOfficialModelProvider _officialModelProvider;
    private readonly IExceptionHandler _exceptionHandler;
    private readonly Assistant _assistant;

    public OfficialModelDefinitionForm(IServiceProvider serviceProvider, Assistant assistant)
    {
        CloudClient = serviceProvider.GetRequiredService<ICloudClient>();
        _officialModelProvider = serviceProvider.GetRequiredService<IOfficialModelProvider>();
        _exceptionHandler = serviceProvider.GetRequiredKeyedService<IExceptionHandler>(typeof(ToastManager));
        _assistant = assistant;

        SelectedItem = _officialModelProvider.ModelDefinitions.FirstOrDefault(m => m.ModelId == assistant.ModelId);
    }

    [RelayCommand]
    private Task RefreshAsync() => _officialModelProvider.RefreshAsync(_exceptionHandler);
}