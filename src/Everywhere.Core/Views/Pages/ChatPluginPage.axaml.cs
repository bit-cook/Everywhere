using Lucide.Avalonia;

namespace Everywhere.Views.Pages;

public partial class ChatPluginPage : ReactiveUserControl<ChatPluginPageViewModel>, IMainViewPage
{
    public int Index => 10;

    public IDynamicResourceKey Title => new DynamicResourceKey(LocaleKey.ChatPluginPage_Title);

    public LucideIconKind Icon => LucideIconKind.Hammer;

    public ChatPluginPage()
    {
        InitializeComponent();
    }
}