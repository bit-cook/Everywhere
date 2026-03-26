using System.ComponentModel;
using Avalonia.Threading;
using Lucide.Avalonia;
using ShadUI;

namespace Everywhere.Configuration;

[GeneratedSettingsItems]
public sealed partial class DisplaySettings : SettingsBase, ISettingsCategory
{
    [HiddenSettingsItem]
    public int Index => 1;

    [HiddenSettingsItem]
    public LucideIconKind Icon => LucideIconKind.MonitorCog;

    [HiddenSettingsItem]
    public IDynamicResourceKey TitleKey { get; } = new DynamicResourceKey(LocaleKey.SettingsCategory_Settings_Display_Header);

    [HiddenSettingsItem]
    public IDynamicResourceKey? DescriptionKey { get; } = new DynamicResourceKey(LocaleKey.SettingsCategory_Settings_Display_Description);

    /// <summary>
    /// Gets or sets the current application language.
    /// </summary>
    /// <remarks>
    /// Warn that this may be "default", which stands for en-US.
    /// </remarks>
    /// <example>
    /// default, zh-hans, ru, de, ja, it, fr, es, ko, zh-hant, zh-hant-hk
    /// </example>
    [DynamicResourceKey(
        LocaleKey.DisplaySettings_Language_Header,
        LocaleKey.DisplaySettings_Language_Description)]
    [TypeConverter(typeof(LocaleNameTypeConverter))]
    public LocaleName Language
    {
        get => LocaleManager.CurrentLocale;
        set
        {
            if (LocaleManager.CurrentLocale == value) return;
            LocaleManager.CurrentLocale = value;
            OnPropertyChanged();
        }
    }

    [DynamicResourceKey(
        LocaleKey.DisplaySettings_Theme_Header,
        LocaleKey.DisplaySettings_Theme_Description)]
    public ThemeMode Theme
    {
        get;
        set
        {
            if (!SetProperty(ref field, value)) return;
            App.ThemeManager.SwitchTheme(value);
        }
    }

    /// <summary>
    /// Application font size.
    /// </summary>
    [SettingsIntegerItem(Min = -1, Max = 3, IsTextBoxVisible = false)]
    [DynamicResourceKey(
        LocaleKey.DisplaySettings_FontSize_Header,
        LocaleKey.DisplaySettings_FontSize_Description)]
    public int FontSize
    {
        get
        {
            return Dispatcher.UIThread.InvokeOnDemand(GetFontSize);

            int GetFontSize()
            {
                if (Application.Current is not { } app) return 0;

                var fontSizeM = app.Resources["FontSizeM"] as double? ?? 14;
                return fontSizeM switch
                {
                    < 14 => -1,
                    15 => 1,
                    16 => 2,
                    > 16 => 3,
                    _ => 0,
                };
            }
        }
        set
        {
            Dispatcher.UIThread.InvokeOnDemand(SetFontSize);
            OnPropertyChanged();

            void SetFontSize()
            {
                if (Application.Current is not { } app) return;

                // <system:Double x:Key="FontSizeS">12.8</system:Double>
                // <system:Double x:Key="FontSizeM">14</system:Double>
                // <system:Double x:Key="FontSizeL">16</system:Double>
                // <system:Double x:Key="FontSizeXl">20</system:Double>
                // <system:Double x:Key="FontSize2Xl">24</system:Double>
                // <system:Double x:Key="FontSize3Xl">30</system:Double>
                // <system:Double x:Key="FontSize4Xl">48</system:Double>

                // value: -1, 0(default), 1, 2, 3

                var fontSizeM = value switch
                {
                    -1 => 12d,
                    1 => 15d,
                    2 => 16d,
                    3 => 18d,
                    _ => 14d,
                };
                app.Resources["FontSizeS"] = fontSizeM * 0.914;
                app.Resources["FontSizeM"] = fontSizeM;
                app.Resources["FontSizeL"] = fontSizeM * 1.142;
                app.Resources["FontSizeXl"] = fontSizeM * 1.428;
                app.Resources["FontSize2Xl"] = fontSizeM * 1.714;
                app.Resources["FontSize3Xl"] = fontSizeM * 2.142;
                app.Resources["FontSize4Xl"] = fontSizeM * 3.428;

                var lineHeightM = fontSizeM / 2 * 3;
                app.Resources["LineHeightS"] = lineHeightM * 0.914;
                app.Resources["LineHeightM"] = lineHeightM;
                app.Resources["LineHeightL"] = lineHeightM * 1.142;
                app.Resources["LineHeightXl"] = lineHeightM * 1.428;
                app.Resources["LineHeight2Xl"] = lineHeightM * 1.714;
                app.Resources["LineHeight3Xl"] = lineHeightM * 2.142;
                app.Resources["LineHeight4Xl"] = lineHeightM * 3.428;
            }
        }
    }
}