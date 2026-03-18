using Lucide.Avalonia;

namespace Everywhere.Views;

public interface IMainViewNavigationItem
{
    int Index { get; }

    LucideIconKind Icon { get; }

    IDynamicResourceKey TitleKey { get; }
}