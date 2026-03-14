using Lucide.Avalonia;

namespace Everywhere.Views;

public interface IMainViewPage
{
    int Index { get; }

    IDynamicResourceKey Title { get; }

    LucideIconKind Icon { get; }
}

public interface IMainViewPageFactory
{
    IEnumerable<IMainViewPage> CreatePages();
}