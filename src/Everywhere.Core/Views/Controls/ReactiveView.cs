using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using ShadUI;

namespace Everywhere.Views;

public abstract class ReactiveUserControl<TViewModel> : UserControl where TViewModel : ReactiveViewModelBase
{
    public TViewModel ViewModel { get; }

    protected ReactiveUserControl(IServiceProvider serviceProvider)
    {
        ViewModel = serviceProvider.GetRequiredService<TViewModel>();
        ViewModel.Bind(this, disposeOnUnloaded: true);
    }
}

public abstract class ReactiveShadWindow<TViewModel> : ShadWindow where TViewModel : ReactiveViewModelBase
{
    public TViewModel ViewModel { get; }

    protected ReactiveShadWindow(IServiceProvider serviceProvider)
    {
        ViewModel = serviceProvider.GetRequiredService<TViewModel>();
        ViewModel.Bind(this);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        ViewModel.Dispose();
    }
}