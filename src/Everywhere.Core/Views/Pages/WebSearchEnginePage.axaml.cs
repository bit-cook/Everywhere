using Lucide.Avalonia;

namespace Everywhere.Views.Pages;

public partial class WebSearchEnginePage : ReactiveUserControl<WebSearchEnginePageViewModel>, IMainViewNavigationTopLevelItem
{
    public int Index => 2;

    public LucideIconKind Icon => LucideIconKind.Globe;

    public IDynamicResourceKey TitleKey { get; } = new DynamicResourceKey(LocaleKey.WebSearchPage_Title);

    public WebSearchEnginePage()
    {
        InitializeComponent();
    }
}