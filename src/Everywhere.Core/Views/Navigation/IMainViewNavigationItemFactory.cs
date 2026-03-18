namespace Everywhere.Views;

public interface IMainViewNavigationItemFactory
{
    IEnumerable<IMainViewNavigationItem> CreateItems();
}