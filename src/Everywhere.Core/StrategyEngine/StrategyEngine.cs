using Everywhere.Chat;
using Microsoft.Extensions.Logging;

namespace Everywhere.StrategyEngine;

/// <summary>
/// Default implementation of <see cref="IStrategyEngine"/>.
/// Orchestrates strategy matching and command generation.
/// </summary>
public sealed class StrategyEngine(IStrategyRegistry registry, ILogger<StrategyEngine> logger) : IStrategyEngine
{
    public IStrategyRegistry Registry { get; } = registry;

    public IReadOnlyList<StrategyGroup> GetStrategies(StrategyContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var results = new List<StrategyGroup>();

        // Evaluate all strategies
        foreach (var strategy in Registry.Strategies)
        {
            try
            {
                if (!strategy.Matches(context)) continue;

                results.Add(new StrategyGroup(strategy, strategy.GetCommands(context).ToReadOnlyList()));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Error evaluating strategy {StrategyId}",
                    strategy.Id);
            }
        }

        // TODO: merge commands and order them based on strategy priority and command metadata (e.g. CommandPriority)

        return results;
    }

    public Task<StrategyExecutionContext> CreateExecutionContextAsync(
        StrategyCommand command,
        StrategyContext context,
        CancellationToken cancellationToken = default)
    {
        // TODO: Implement command execution
        // This will:
        // 1. Resolve template variables
        // 2. Configure allowed tools

        return Task.FromResult(new StrategyExecutionContext
        {
            SystemPrompt = command.SystemPrompt,
            UserChatMessage = new UserChatMessage(command.UserMessage ?? string.Empty, context.Attachments)
        });
    }
}
