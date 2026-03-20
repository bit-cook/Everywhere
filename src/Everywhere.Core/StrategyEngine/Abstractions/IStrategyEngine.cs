namespace Everywhere.StrategyEngine;

/// <summary>
/// The main entry point for the Strategy Engine.
/// Orchestrates strategy matching and command generation.
/// </summary>
public interface IStrategyEngine
{
    /// <summary>
    /// The strategy registry containing all available strategies.
    /// </summary>
    IStrategyRegistry Registry { get; }

    /// <summary>
    /// Evaluates all strategies against the current context and returns matching groups.
    /// Commands are merged, deduplicated, and sorted by priority.
    /// </summary>
    /// <param name="context">The strategy context to evaluate.</param>
    /// <returns>List of matching commands, sorted by priority (descending).</returns>
    IReadOnlyList<StrategyGroup> GetStrategies(StrategyContext context);

    /// <summary>
    /// Executes a command, starting an agent session with the configured prompt and tools.
    /// </summary>
    /// <param name="command">The command to execute.</param>
    /// <param name="context">The original strategy context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<StrategyExecutionContext> CreateExecutionContextAsync(
        StrategyCommand command,
        StrategyContext context,
        CancellationToken cancellationToken = default);
}