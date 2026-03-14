using Lucide.Avalonia;

namespace Everywhere.Views.Pages;

public partial class AboutPage : ReactiveUserControl<AboutPageViewModel>, IMainViewPage
{
    public int Index => int.MaxValue;

    public IDynamicResourceKey Title => new DynamicResourceKey(LocaleKey.AboutPage_Title);

    public LucideIconKind Icon => LucideIconKind.Info;

    public AboutPage()
    {
        InitializeComponent();
    }
}