using System.Runtime.Versioning;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Common;
using Everywhere.Interop;
using Everywhere.Views;
using Lucide.Avalonia;
using Serilog;
using ShadUI;

namespace Everywhere.Configuration;

[GeneratedSettingsItems]
public sealed partial class CommonSettings : ObservableObject, IMainViewNavigationSubItem
{
    private static INativeHelper NativeHelper => ServiceLocator.Resolve<INativeHelper>();

    [HiddenSettingsItem]
    public int Index => 0;

    [HiddenSettingsItem]
    public LucideIconKind Icon => LucideIconKind.Blocks;

    [HiddenSettingsItem]
    public IDynamicResourceKey TitleKey { get; } = new DynamicResourceKey(LocaleKey.SettingsCategory_Settings_Common_Header);

    [HiddenSettingsItem]
    public Type GroupType => typeof(SettingsCategory);

    [HiddenSettingsItem]
    public IDynamicResourceKey? DescriptionKey { get; } = new DynamicResourceKey(LocaleKey.SettingsCategory_Settings_Common_Description);

    [ObservableProperty]
    [HiddenSettingsItem]
    public partial DateTimeOffset? LastUpdateCheckTime { get; set; }

    [JsonIgnore]
    [DynamicResourceKey(
        LocaleKey.SoftwareSettings_SoftwareUpdate_Header,
        LocaleKey.SoftwareSettings_SoftwareUpdate_Description)]
    public SettingsControl<SoftwareUpdateControl> SoftwareUpdate { get; } = new();

    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.SoftwareSettings_IsAutomaticUpdateCheckEnabled_Header,
        LocaleKey.SoftwareSettings_IsAutomaticUpdateCheckEnabled_Description)]
    public partial bool IsAutomaticUpdateCheckEnabled { get; set; } = true;

#if WINDOWS
    [JsonIgnore]
    [HiddenSettingsItem]
    [SupportedOSPlatform("windows")]
    public static bool IsAdministrator => NativeHelper.IsAdministrator;

    [JsonIgnore]
    [DynamicResourceKey(
        LocaleKey.SoftwareSettings_RestartAsAdministrator_Header,
        LocaleKey.SoftwareSettings_RestartAsAdministrator_Description)]
    [SettingsItem(IsVisibleBindingPath = $"!{nameof(IsAdministrator)}")]
    [SupportedOSPlatform("windows")]
    public SettingsControl<RestartAsAdministratorControl> RestartAsAdministrator { get; } = new();

    [JsonIgnore]
    [DynamicResourceKey(
        LocaleKey.SoftwareSettings_IsStartupEnabled_Header,
        LocaleKey.SoftwareSettings_IsStartupEnabled_Description)]
    [SettingsItem(IsEnabledBindingPath = $"{nameof(IsAdministrator)} || !{nameof(IsAdministratorStartupEnabled)}")]
    [SupportedOSPlatform("windows")]
    public bool IsStartupEnabled
    {
        get => NativeHelper.IsUserStartupEnabled || NativeHelper.IsAdministratorStartupEnabled;
        set
        {
            try
            {
                // If disabling user startup while admin startup is enabled, also disable admin startup.
                if (!value && NativeHelper.IsAdministratorStartupEnabled)
                {
                    if (IsAdministrator)
                    {
                        NativeHelper.IsAdministratorStartupEnabled = false;
                        OnPropertyChanged(nameof(IsAdministratorStartupEnabled));
                    }
                    else
                    {
                        return;
                    }
                }

                NativeHelper.IsUserStartupEnabled = value;
                OnPropertyChanged();
            }
            catch (Exception ex)
            {
                ex = HandledSystemException.Handle(ex); // maybe blocked by UAC or antivirus, handle it gracefully
                Log.ForContext<CommonSettings>().Error(ex, "Failed to set user startup enabled.");
                ShowErrorToast(ex);
            }
        }
    }

    [JsonIgnore]
    [DynamicResourceKey(
        LocaleKey.SoftwareSettings_IsAdministratorStartupEnabled_Header,
        LocaleKey.SoftwareSettings_IsAdministratorStartupEnabled_Description)]
    [SettingsItem(IsVisibleBindingPath = nameof(IsStartupEnabled), IsEnabledBindingPath = nameof(IsAdministrator))]
    [SupportedOSPlatform("windows")]
    public bool IsAdministratorStartupEnabled
    {
        get => NativeHelper.IsAdministratorStartupEnabled;
        set
        {
            try
            {
                if (!IsAdministrator) return;

                // If enabling admin startup while user startup is disabled, also enable user startup.
                NativeHelper.IsUserStartupEnabled = !value;
                NativeHelper.IsAdministratorStartupEnabled = value;
            }
            catch (Exception ex)
            {
                ex = HandledSystemException.Handle(ex); // maybe blocked by UAC or antivirus, handle it gracefully
                Log.ForContext<CommonSettings>().Error(ex, "Failed to set administrator startup enabled.");
                ShowErrorToast(ex);
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(IsStartupEnabled));
        }
    }
#else
    [JsonIgnore]
    [DynamicResourceKey(
        LocaleKey.SoftwareSettings_IsUserStartupEnabled_Header,
        LocaleKey.SoftwareSettings_IsUserStartupEnabled_Description)]
    public bool IsUserStartupEnabled
    {
        get => NativeHelper.IsUserStartupEnabled;
        set
        {
            try
            {
                NativeHelper.IsUserStartupEnabled = value;
                OnPropertyChanged();
            }
            catch (Exception ex)
            {
                ex = HandledSystemException.Handle(ex); // maybe blocked by UAC or antivirus, handle it gracefully
                Log.ForContext<CommonSettings>().Error(ex, "Failed to set user startup enabled.");
                ShowErrorToast(ex);
            }
        }
    }
#endif

    [DynamicResourceKey(
        LocaleKey.SoftwareSettings_DiagnosticData_Header,
        LocaleKey.SoftwareSettings_DiagnosticData_Description)]
    public bool DiagnosticData
    {
        get => !Entrance.SendOnlyNecessaryTelemetry;
        set
        {
            Entrance.SendOnlyNecessaryTelemetry = !value;
            OnPropertyChanged();
        }
    }

    [JsonIgnore]
    [DynamicResourceKey(
        LocaleKey.SoftwareSettings_DebugFeatures_Header,
        LocaleKey.SoftwareSettings_DebugFeatures_Description)]
    public SettingsControl<DebugFeaturesControl> DebugFeatures { get; } = new();

    private static void ShowErrorToast(Exception ex) => ServiceLocator.Resolve<ToastManager>()
        .CreateToast(LocaleResolver.Common_Error)
        .WithContent(ex.GetFriendlyMessage())
        .DismissOnClick()
        .OnBottomRight()
        .ShowError();
}