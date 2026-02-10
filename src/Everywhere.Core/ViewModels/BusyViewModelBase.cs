using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Common;

namespace Everywhere.ViewModels;

[Flags]
public enum ExecutionFlags
{
    None = 0,
    EnqueueIfBusy = 1,
}

public abstract partial class BusyViewModelBase : ReactiveViewModelBase
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    public partial bool IsBusy { get; private set; }

    public bool IsNotBusy => !IsBusy;

    private Task? _currentTask;
    private readonly SemaphoreSlim _executionLock = new(1, 1);

    protected async Task ExecuteBusyTaskAsync(
        Func<CancellationToken, Task> task,
        IExceptionHandler? exceptionHandler = null,
        ExecutionFlags flags = ExecutionFlags.None,
        CancellationToken cancellationToken = default)
    {
        await _executionLock.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!flags.HasFlag(ExecutionFlags.EnqueueIfBusy) && IsBusy) return;

            Task taskToWait;
            if (_currentTask is { IsCompleted: false })
            {
                taskToWait = _currentTask;
                _currentTask = _currentTask.ContinueWith(
                    async _ =>
                    {
                        try { await task(cancellationToken); }
                        catch when (cancellationToken.IsCancellationRequested) { }
                    },
                    TaskContinuationOptions.RunContinuationsAsynchronously);
            }
            else
            {
                taskToWait = _currentTask = task(cancellationToken);
            }

            try
            {
                IsBusy = true;
                await taskToWait;
            }
            catch (OperationCanceledException) { }
            catch (Exception e) when (exceptionHandler != null)
            {
                exceptionHandler.HandleException(e);
            }
            finally
            {
                IsBusy = _currentTask is
                {
                    Status: TaskStatus.WaitingToRun or TaskStatus.Running or TaskStatus.WaitingForChildrenToComplete
                };
            }
        }
        finally
        {
            _executionLock.Release();
        }
    }

    protected Task ExecuteBusyTaskAsync(
        Action<CancellationToken> action,
        IExceptionHandler? exceptionHandler = null,
        ExecutionFlags flags = ExecutionFlags.None,
        CancellationToken cancellationToken = default) => ExecuteBusyTaskAsync(
        token =>
        {
            action(token);
            return Task.CompletedTask;
        },
        exceptionHandler,
        flags,
        cancellationToken);

    // ReSharper disable once UnusedParameterInPartialMethod
    partial void OnIsBusyChanged(bool value) => OnIsBusyChanged();

    /// <summary>
    /// Invoked when the value of <see cref="IsBusy"/> changes.
    /// </summary>
    protected virtual void OnIsBusyChanged() { }
}