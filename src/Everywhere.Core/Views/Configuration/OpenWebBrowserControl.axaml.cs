using Avalonia.Controls.Primitives;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Common;
using Everywhere.Web;
using Microsoft.Extensions.Logging;
using ShadUI;

namespace Everywhere.Views;

public partial class OpenWebBrowserControl(
    IWebBrowserHost webBrowserHost,
    ToastManager toastManager,
    ILogger<RestartAsAdministratorControl> logger
) : TemplatedControl
{
    [RelayCommand]
    private async Task OpenBrowserAsync()
    {
        try
        {
            await webBrowserHost.OpenBrowserAsync();
        }
        catch (Exception ex)
        {
            ex = HandledSystemException.Handle(ex);
            logger.LogInformation(ex, "Failed to open web browser");
            toastManager
                .CreateToast(LocaleResolver.Common_Error)
                .WithContent(ex.GetFriendlyMessage())
                .DismissOnClick()
                .OnBottomRight()
                .ShowError();
        }
    }
}