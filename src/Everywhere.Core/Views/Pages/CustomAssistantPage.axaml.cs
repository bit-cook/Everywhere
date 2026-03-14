using Lucide.Avalonia;

namespace Everywhere.Views.Pages;

public partial class CustomAssistantPage : ReactiveUserControl<CustomAssistantPageViewModel>, IMainViewPage
{
    public int Index => 9;

    public IDynamicResourceKey Title => new DynamicResourceKey(LocaleKey.CustomAssistantPage_Title);

    public LucideIconKind Icon => LucideIconKind.Bot;

    public CustomAssistantPage()
    {
        InitializeComponent();
    }
}