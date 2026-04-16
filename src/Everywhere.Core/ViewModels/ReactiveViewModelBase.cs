using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Common;
using Everywhere.Interop;
using Everywhere.Utilities;
using Everywhere.Views;
using ShadUI;

namespace Everywhere.ViewModels;

public abstract class ReactiveViewModelBase : ObservableValidator
{
    [field: AllowNull, MaybeNull]
    protected DialogManager DialogManager
    {
        get => field ??= ServiceLocator.Resolve<DialogManager>();
        private set;
    }

    [field: AllowNull, MaybeNull]
    protected ToastManager ToastManager
    {
        get => field ??= ServiceLocator.Resolve<ToastManager>();
        private set;
    }

    protected IClipboard Clipboard =>
        _topLevel?.Clipboard ?? throw new InvalidOperationException("Clipboard is not available.");

    protected IStorageProvider StorageProvider =>
        _topLevel?.StorageProvider?? throw new InvalidOperationException("StorageProvider is not available.");

    protected static ILauncher Launcher => BetterBclLauncher.Shared;

    private TopLevel? _topLevel;

    protected AnonymousExceptionHandler DialogExceptionHandler => new((exception, message, source, lineNumber) =>
        DialogManager.CreateDialog(exception.GetFriendlyMessage().ToString() ?? "Unknown error", $"[{source}:{lineNumber}] {message ?? "Error"}"));

    protected AnonymousExceptionHandler ToastExceptionHandler => new((exception, message, source, lineNumber) =>
        ToastManager.CreateToast($"[{source}:{lineNumber}] {message ?? "Error"}")
            .WithContent(exception.GetFriendlyMessage().ToTextBlock())
            .DismissOnClick()
            .ShowError());

    /// <summary>
    /// Invoked when the view's <see cref="Control.Loaded"/> event is raised.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    protected internal virtual Task ViewLoaded(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Invoked when the view's <see cref="Control.Unloaded"/> event is raised.
    /// </summary>
    /// <returns></returns>
    protected internal virtual Task ViewUnloaded() => Task.CompletedTask;

    protected virtual IExceptionHandler? LifetimeExceptionHandler => null;

    private void HandleLifetimeException(string stage, Exception e)
    {
        var handler = LifetimeExceptionHandler ?? DialogManager.ToExceptionHandler();
        handler.HandleException(e, $"Lifetime Exception: [{stage}]");
    }

    public void Bind(Control target)
    {
        target.DataContext = this;

        var cancellationTokenSource = new ReusableCancellationTokenSource();
        target.Loaded += async (_, _) =>
        {
            try
            {
                _topLevel = TopLevel.GetTopLevel(target);
                if (_topLevel is IReactiveHost reactiveHost)
                {
                    DialogManager = reactiveHost.DialogHost.Manager;
                    ToastManager = reactiveHost.ToastHost.Manager;
                }

                await ViewLoaded(cancellationTokenSource.Token);
            }
            catch (Exception e)
            {
                HandleLifetimeException(nameof(ViewLoaded), e);
            }
        };

        target.Unloaded += async (_, _) =>
        {
            _topLevel = null;
            cancellationTokenSource.Cancel();

            try
            {
                await ViewUnloaded();
            }
            catch (Exception e)
            {
                HandleLifetimeException(nameof(ViewUnloaded), e);
            }
        };
    }
}