using Everywhere.Views;
using Lucide.Avalonia;

namespace Everywhere.Configuration;

public sealed class SettingsCategory : IMainViewNavigationTopLevelItem
{
    public int Index => 1;

    public LucideIconKind Icon => LucideIconKind.Cog;

    public IDynamicResourceKey TitleKey { get; } = new DynamicResourceKey(LocaleKey.SettingsCategory_Settings_Header);
}