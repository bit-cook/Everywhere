namespace Everywhere.StrategyEngine;

public sealed record StrategiesSnapshot(
    StrategyContext Context,
    IReadOnlyList<StrategyGroup> StrategyGroups
)
{
    public IEnumerable<StrategyCommand> StrategyCommands => StrategyGroups.SelectMany(g => g.Commands).OrderByDescending(c => c.Priority);
}