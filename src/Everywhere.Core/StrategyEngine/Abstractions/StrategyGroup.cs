namespace Everywhere.StrategyEngine;

public readonly record struct StrategyGroup(IStrategy Strategy, IReadOnlyList<StrategyCommand> Commands);