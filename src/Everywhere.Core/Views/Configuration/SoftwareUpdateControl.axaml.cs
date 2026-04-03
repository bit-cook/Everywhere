using Avalonia.Controls.Primitives;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Common;
using Everywhere.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ShadUI;

namespace Everywhere.Views;

public partial class SoftwareUpdateControl(
    Settings settings,
    ISoftwareUpdater softwareUpdater,
    ToastManager toastManager,
    IServiceProvider serviceProvider,
    ILogger<SoftwareUpdateControl> logger
) : TemplatedControl
{
    public Settings Settings { get; } = settings;

    public ISoftwareUpdater SoftwareUpdater { get; } = softwareUpdater;

    public static readonly StyledProperty<IDynamicResourceKey?> UpdateOrCheckTitleProperty = AvaloniaProperty.Register<SoftwareUpdateControl, IDynamicResourceKey?>(
        nameof(UpdateOrCheckTitle));

    public IDynamicResourceKey? UpdateOrCheckTitle
    {
        get => GetValue(UpdateOrCheckTitleProperty);
        set => SetValue(UpdateOrCheckTitleProperty, value);
    }

    [RelayCommand]
    private async Task UpdateOrCheckAsync()
    {
        UpdateOrCheckTitle = new DynamicResourceKey(LocaleKey.CommonSettings_SoftwareUpdate_CheckingUpdateTitle_Text);
        if (SoftwareUpdater.LatestVersion is not null)
        {
            serviceProvider.GetRequiredService<MainViewModel>().NavigateTo(serviceProvider.GetRequiredService<ChangeLogView>());
            return;
        }
        
        try
        {
            await SoftwareUpdater.CheckForUpdatesAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to check for updates.");

            ex = new HandledException(ex, new DynamicResourceKey(LocaleKey.CommonSettings_SoftwareUpdate_Toast_CheckForUpdatesFailed_Content));
            toastManager
                .CreateToast(LocaleResolver.Common_Error)
                .WithContent(ex.GetFriendlyMessage())
                .DismissOnClick()
                .OnBottomRight()
                .ShowError();
        }
    }
}