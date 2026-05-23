using Microsoft.Extensions.Logging;

namespace Everywhere.Terminal;

/// <summary>
/// Result of executing a command in the terminal.
/// </summary>
/// <param name="Output">The cleaned command output text.</param>
/// <param name="ExitCode">The exit code of the command, if available.</param>
public readonly record struct ExecuteResult(string Output, int? ExitCode);

/// <summary>
/// Strategy for executing commands in a PTY and capturing their output.
/// Implementations differ based on whether Shell Integration is available.
/// </summary>
public interface IExecuteStrategy
{
    /// <summary>
    /// Execute a command in the PTY and return the cleaned output.
    /// </summary>
    /// <param name="session">The terminal session to use.</param>
    /// <param name="script">The script to execute.</param>
    /// <param name="shellType"></param>
    /// <param name="timeout">Maximum time to wait for the command to complete.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The execution result containing output and exit code.</returns>
    Task<ExecuteResult> ExecuteAsync(
        TerminalSession session,
        string script,
        ShellType shellType,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    /// <summary>
    /// Detect whether Shell Integration is available by reading initial PTY output
    /// and checking for OSC 633 markers. Returns the appropriate strategy.
    /// </summary>
    static async Task<IExecuteStrategy> DetectStrategyAsync(
        TerminalSession session,
        ShellType shellType,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        // Read initial output for a short period to detect shell integration markers
        var absoluteTimeout = TimeSpan.FromSeconds(10);
        var idleTimeout = TimeSpan.FromSeconds(3);
        var startTime = DateTimeOffset.UtcNow;

        logger.LogDebug(
            "[Detect] Starting Shell Integration detection for {ShellType} (Idle: {Idle}s, Max: {Max}s)",
            shellType,
            idleTimeout.TotalSeconds,
            absoluteTimeout.TotalSeconds);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var remainingAbsolute = absoluteTimeout - (DateTimeOffset.UtcNow - startTime);
            if (remainingAbsolute <= TimeSpan.Zero)
            {
                logger.LogWarning("[Detect] Absolute timeout reached ({AbsoluteTimeout}s). Stream was too noisy.", absoluteTimeout.TotalSeconds);
                break;
            }

            var readTimeout = remainingAbsolute < idleTimeout ? remainingAbsolute : idleTimeout;
            using var readTimeoutCts = new CancellationTokenSource(readTimeout);
            using var linkedReadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, readTimeoutCts.Token);

            int bytesRead;
            try
            {
                _ = session.BeginReadAsync(linkedReadCts.Token);
                bytesRead = await session.CompleteReadAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && readTimeoutCts.IsCancellationRequested)
            {
                if (DateTimeOffset.UtcNow - startTime >= absoluteTimeout)
                {
                    logger.LogWarning(
                        "[Detect] Absolute timeout reached ({AbsoluteTimeout}s). Stream was too noisy.",
                        absoluteTimeout.TotalSeconds);
                }
                else
                {
                    logger.LogInformation(
                        "[Detect] Idle timeout reached ({IdleTimeout}s). Marker not found, output settled.",
                        idleTimeout.TotalSeconds);
                }

                break;
            }

            if (bytesRead == 0)
            {
                if (session.Parser.HasDetectedShellIntegration)
                {
                    logger.LogDebug(
                        "[Detect] Shell Integration detected for {ShellType} in {Seconds}s, using Rich strategy",
                        shellType,
                        (DateTimeOffset.UtcNow - startTime).TotalSeconds);

                    return new RichExecuteStrategy(logger);
                }

                logger.LogWarning("[Detect] PTY stream closed unexpectedly during detection.");
                break;
            }

            if (session.Parser.HasDetectedShellIntegration)
            {
                logger.LogDebug(
                    "[Detect] Shell Integration detected for {ShellType} in {Seconds}s, using Rich strategy",
                    shellType,
                    (DateTimeOffset.UtcNow - startTime).TotalSeconds);

                return new RichExecuteStrategy(logger);
            }
        }

        logger.LogInformation(
            "[Detect] Falling back to None strategy for {ShellType} in {Seconds}s.",
            shellType,
            (DateTimeOffset.UtcNow - startTime).TotalSeconds);

        return new NoneExecuteStrategy(logger);
    }
}
