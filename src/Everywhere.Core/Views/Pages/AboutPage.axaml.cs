using Lucide.Avalonia;

namespace Everywhere.Views.Pages;

public partial class AboutPage : ReactiveUserControl<AboutPageViewModel>, IMainViewNavigationTopLevelItem
{
    public int Index => int.MaxValue;

    public LucideIconKind Icon => LucideIconKind.Info;

    public IDynamicResourceKey TitleKey { get; } = new DynamicResourceKey(LocaleKey.AboutPage_Title);

    public AboutPage()
    {
        InitializeComponent();
    }
}