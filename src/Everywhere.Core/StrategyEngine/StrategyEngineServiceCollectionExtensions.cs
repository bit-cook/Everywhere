using Everywhere.Common;
using Everywhere.Initialization;
using Everywhere.StrategyEngine.BuiltIn;
using Microsoft.Extensions.DependencyInjection;

namespace Everywhere.StrategyEngine;

/// <summary>
/// Extension methods for registering Strategy Engine services.
/// </summary>
public static class StrategyEngineServiceCollectionExtensions
{
    /// <summary>
    /// Adds the Strategy Engine services to the service collection.
    /// </summary>
    public static IServiceCollection AddStrategyEngine(this IServiceCollection services)
    {
        // Register core services
        services.AddSingleton<IStrategyRegistry, StrategyRegistry>();
        services.AddSingleton<IStrategyEngine, StrategyEngine>();
        services.AddSingleton<IAsyncInitializer, StrategyEngineInitializer>();

        // Register built-in strategies
        services.AddSingleton<IBuiltInStrategy, GlobalStrategy>();
        services.AddSingleton<IBuiltInStrategy, BrowserStrategy>();
        services.AddSingleton<IBuiltInStrategy, CodeEditorStrategy>();
        services.AddSingleton<IBuiltInStrategy, TextSelectionStrategy>();
        services.AddSingleton<IBuiltInStrategy, FileStrategy>();

        return services;
    }
}
