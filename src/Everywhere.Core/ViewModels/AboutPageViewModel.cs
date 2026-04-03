using CommunityToolkit.Mvvm.Input;
using Everywhere.Views;
using Everywhere.Views.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace Everywhere.ViewModels;

public partial class AboutPageViewModel(IServiceProvider serviceProvider) : ReactiveViewModelBase
{
    public static string Version => typeof(AboutPage).Assembly.GetName().Version?.ToString() ?? "Unknown Version";

    [RelayCommand]
    private void OpenWelcomeDialog()
    {
        DialogManager
            .CreateCustomDialog(serviceProvider.GetRequiredService<WelcomeView>())
            .ShowAsync();
    }

    [RelayCommand]
    private void OpenChangeLogDialog()
    {
        serviceProvider.GetRequiredService<MainViewModel>().NavigateTo(serviceProvider.GetRequiredService<ChangeLogView>());
    }
}