namespace Everywhere.StrategyEngine;

public sealed record StrategiesSnapshot(
    StrategyContext Context,
    IReadOnlyList<StrategyGroup> StrategyGroups
);