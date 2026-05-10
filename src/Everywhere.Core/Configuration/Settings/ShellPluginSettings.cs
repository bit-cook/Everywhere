using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Common;
using ShadUI;

namespace Everywhere.Configuration;

[GeneratedSettingsItems]
public sealed partial class ShellPluginSettings : ObservableObject
{
    [DynamicResourceKey(
        LocaleKey.ShellPluginSettings_AutoApprove_Header,
        LocaleKey.ShellPluginSettings_AutoApprove_Description)]
    public bool AutoApprove
    {
        get;
        set
        {
            if (!SetProperty(ref field, value)) return;

            if (value)
            {
                Dispatcher.UIThread.PostOnDemand(() => ServiceLocator.Resolve<ToastManager>().CreateToast(LocaleResolver.Common_Warning)
                    .WithContent(LocaleResolver.ShellPluginSettings_AutoApprove_WarningToast_Content)
                    .WithDurationSeconds(5d)
                    .ShowWarning());
            }
        }
    }
}