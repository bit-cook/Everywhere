using Nito.AsyncEx;

namespace Everywhere.Database;

/// <summary>
/// Provides a static signaling mechanism for notifying the cloud synchronizer
/// that local data has changed and a sync cycle should be initiated promptly.
/// Static because <see cref="SyncSaveChangesInterceptor"/> is instantiated via <c>new</c> without DI access.
/// </summary>
public static class CloudSyncTrigger
{
    private static readonly AsyncAutoResetEvent ChangeEvent = new(false);

    /// <summary>
    /// Signals that local syncable data has been modified.
    /// Called by <see cref="SyncSaveChangesInterceptor"/> after assigning version numbers.
    /// </summary>
    public static void SignalLocalChange() => ChangeEvent.Set();

    /// <summary>
    /// Asynchronously waits until a local change is signaled or cancellation is requested.
    /// </summary>
    public static Task WaitForChangeAsync(CancellationToken cancellationToken = default)
        => ChangeEvent.WaitAsync(cancellationToken);
}
