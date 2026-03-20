using Everywhere.Common;
using Everywhere.StrategyEngine;
using Microsoft.Extensions.DependencyInjection;

namespace Everywhere.Initialization;

public class StrategyEngineInitializer(IServiceProvider serviceProvider) : IAsyncInitializer
{
    public AsyncInitializerIndex Index => AsyncInitializerIndex.Startup;

    public Task InitializeAsync()
    {
        var registry = serviceProvider.GetRequiredService<IStrategyRegistry>();
        var strategies = serviceProvider.GetServices<IBuiltInStrategy>();
        registry.RegisterRange(strategies);

        return Task.CompletedTask;
    }
}