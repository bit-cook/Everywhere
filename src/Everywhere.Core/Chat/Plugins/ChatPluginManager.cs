using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Reactive.Disposables;
using System.Text.RegularExpressions;
using DynamicData;
using Everywhere.Chat.Permissions;
using Everywhere.Chat.Plugins.Mcp;
using Everywhere.Collections;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Utilities;
using FuzzySharp;
using Lucide.Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using ZLinq;

namespace Everywhere.Chat.Plugins;

public class ChatPluginManager : IChatPluginManager
{
    public ReadOnlyObservableCollection<BuiltInChatPlugin> BuiltInPlugins { get; }

    public ReadOnlyObservableCollection<McpChatPlugin> McpPlugins { get; }

    private readonly IServiceProvider _serviceProvider;
    private readonly Settings _settings;

    private readonly ConcurrentDictionary<Guid, ManagedMcpClient> _managedClients = [];
    private readonly CompositeDisposable _disposables = new(3);
    private readonly SourceList<BuiltInChatPlugin> _builtInPluginsSource = new();
    private readonly SourceList<McpChatPlugin> _mcpPluginsSource = new();

    public ChatPluginManager(
        IServiceProvider serviceProvider,
        IEnumerable<BuiltInChatPlugin> builtInPlugins,
        Settings settings,
        ILogger<ChatPluginManager> logger)
    {
        _serviceProvider = serviceProvider;
        _builtInPluginsSource.AddRange(builtInPlugins);
        _settings = settings;

        // Load MCP plugins from settings.
        var mcpPlugins = settings.Plugin.McpChatPlugins.AsValueEnumerable().Select(m => m.ToMcpChatPlugin()).OfType<McpChatPlugin>().ToList();
        Task.Run(InitializeMcpPlugins).Detach(IExceptionHandler.DangerouslyIgnoreAllException);
        _mcpPluginsSource.AddRange(mcpPlugins);

        // Apply the enabled state from settings.
        var isEnabledRecords = settings.Plugin.IsEnabledRecords;
        var isPermissionGrantedRecords = settings.Plugin.IsPermissionGrantedRecords;
        var pluginKeys = new HashSet<string>();
        foreach (var plugin in _builtInPluginsSource.Items.AsValueEnumerable().OfType<ChatPlugin>().Concat(_mcpPluginsSource.Items))
        {
            pluginKeys.Add(plugin.Key);
            plugin.IsEnabled = GetIsEnabled(plugin.Key, plugin is BuiltInChatPlugin { IsDefaultEnabled: true });
            foreach (var function in plugin.Functions)
            {
                var key = $"{plugin.Key}.{function.KernelFunction.Name}";
                function.IsEnabled = GetIsEnabled(key, true);
                function.AutoApprove = function.IsAutoApproveAllowed && GetIsPermissionGranted(key, function.Permissions);
            }
        }

        // Remove any records in settings that do not correspond to any existing plugin.
        foreach (var key in isEnabledRecords.Keys.AsValueEnumerable().ToList())
        {
            if (pluginKeys.All(k => k != key && !key.StartsWith($"{k}.", StringComparison.Ordinal)))
            {
                isEnabledRecords.Remove(key);
            }
        }
        foreach (var key in isPermissionGrantedRecords.Keys.AsValueEnumerable().ToList())
        {
            if (pluginKeys.All(k => k != key && !key.StartsWith($"{k}.", StringComparison.Ordinal)))
            {
                isPermissionGrantedRecords.Remove(key);
            }
        }

        BuiltInPlugins = _builtInPluginsSource
            .Connect()
            .ObserveOnAvaloniaDispatcher()
            .BindEx(_disposables);
        _disposables.Add(_builtInPluginsSource);

        McpPlugins = _mcpPluginsSource
            .Connect()
            .ObserveOnAvaloniaDispatcher()
            .BindEx(_disposables);
        _disposables.Add(_mcpPluginsSource);

        settings.Plugin.McpChatPlugins = _mcpPluginsSource
            .Connect()
            .AutoRefresh(m => m.TransportConfiguration)
            .ObserveOnAvaloniaDispatcher()
            .Transform(m => new McpChatPluginEntity(m), transformOnRefresh: true)
            .BindEx(_disposables);

        new ObjectObserver((in e) => HandleChatPluginChanged(BuiltInPlugins, e)).Observe(BuiltInPlugins);
        new ObjectObserver((in e) => HandleChatPluginChanged(McpPlugins, e)).Observe(McpPlugins);

        void InitializeMcpPlugins()
        {
            foreach (var mcpPlugin in mcpPlugins)
            {
                try
                {
                    GetOrCreateClient(mcpPlugin);
                }
                catch (Exception e)
                {
                    logger.LogError(e, "An error occured while initializing the MCP plugin");
                }
            }
        }

        // Helper method to get the enabled state from settings.
        bool GetIsEnabled(string path, bool defaultValue)
        {
            return isEnabledRecords.TryGetValue(path, out var isEnabled) ? isEnabled : defaultValue;
        }

        bool GetIsPermissionGranted(string path, ChatFunctionPermissions permissions)
        {
            if (isPermissionGrantedRecords.TryGetValue(path, out var isGranted) && !isGranted) return false;
            if (isGranted) return true;
            return permissions <= ChatFunctionPermissions.AutoGranted;
        }

        // Handle changes to plugins and update settings accordingly.
        void HandleChatPluginChanged<TPlugin>(IReadOnlyList<TPlugin> plugins, in ObjectObserverChangedEventArgs e) where TPlugin : ChatPlugin
        {
            var parts = e.Path.Split(':');
            if (parts.Length < 2 || !int.TryParse(parts[0], out var pluginIndex) || pluginIndex < 0 || pluginIndex >= plugins.Count)
            {
                return;
            }

            var plugin = plugins[pluginIndex];
            var value = e.Value is true;

            ObservableDictionary<string, bool> records;
            bool? defaultValue;
            if (e.Path.EndsWith(nameof(ChatFunction.IsEnabled), StringComparison.Ordinal))
            {
                records = isEnabledRecords;
                defaultValue = parts.Length != 2 || plugin is BuiltInChatPlugin { IsDefaultEnabled: true };
            }
            else if (e.Path.EndsWith(nameof(ChatFunction.AutoApprove), StringComparison.Ordinal))
            {
                records = isPermissionGrantedRecords;
                defaultValue = null;
            }
            else
            {
                return;
            }

            string key;
            switch (parts.Length)
            {
                case 2:
                {
                    key = plugin.Key;
                    break;
                }
                case 4 when
                    int.TryParse(parts[2], out var functionIndex) &&
                    functionIndex >= 0 &&
                    functionIndex < plugin.Functions.Count:
                {
                    var function = plugin.Functions[functionIndex];
                    key = $"{plugin.Key}.{function.KernelFunction.Name}";
                    break;
                }
                default:
                {
                    throw new InvalidOperationException($"Unexpected change path: {e.Path}");
                }
            }

            if (value == defaultValue) records.Remove(key);
            else records[key] = value;
        }
    }

    public McpChatPlugin CreateMcpPlugin(McpTransportConfiguration configuration)
    {
        if (configuration.HasErrors)
        {
            throw new HandledException(
                new InvalidOperationException("MCP transport configuration is not valid."),
                new DynamicResourceKey(LocaleKey.ChatPluginManager_Common_InvalidMcpTransportConfiguration));
        }

        var mcpChatPlugin = new McpChatPlugin(configuration);
        GetOrCreateClient(mcpChatPlugin);
        _mcpPluginsSource.Add(mcpChatPlugin);
        return mcpChatPlugin;
    }

    public async Task UpdateMcpPluginAsync(McpChatPlugin mcpChatPlugin, McpTransportConfiguration configuration)
    {
        if (configuration.HasErrors)
        {
            throw new HandledException(
                new InvalidOperationException("MCP transport configuration is not valid."),
                new DynamicResourceKey(LocaleKey.ChatPluginManager_Common_InvalidMcpTransportConfiguration));
        }

        var wasRunning = mcpChatPlugin.IsRunning;
        if (wasRunning)
        {
            await StopMcpClientAsync(mcpChatPlugin);
        }

        mcpChatPlugin.TransportConfiguration = configuration;

        if (wasRunning)
        {
            await StartMcpClientAsync(mcpChatPlugin, CancellationToken.None);
        }
    }

    private ManagedMcpClient GetOrCreateClient(McpChatPlugin mcpChatPlugin)
    {
        if (_managedClients.TryGetValue(mcpChatPlugin.Id, out var existingClient))
        {
            return existingClient;
        }

        var client = new ManagedMcpClient(
            mcpChatPlugin,
            this,
            _serviceProvider,
            new McpLoggerFactory(mcpChatPlugin, _serviceProvider.GetRequiredService<ILoggerFactory>()),
            _settings.Plugin);

        _managedClients[mcpChatPlugin.Id] = client;
        return client;
    }

    public async Task StartMcpClientAsync(McpChatPlugin mcpChatPlugin, CancellationToken cancellationToken)
    {
        if (mcpChatPlugin.TransportConfiguration is not { } transportConfiguration)
        {
            throw new HandledException(
                new InvalidOperationException("MCP transport configuration is not set."),
                new DynamicResourceKey(LocaleKey.ChatPluginManager_Common_InvalidMcpTransportConfiguration));
        }

        if (transportConfiguration.HasErrors)
        {
            throw new HandledException(
                new InvalidOperationException("MCP transport configuration is not valid."),
                new DynamicResourceKey(LocaleKey.ChatPluginManager_Common_InvalidMcpTransportConfiguration));
        }

        var client = GetOrCreateClient(mcpChatPlugin);
        await client.StartAsync(cancellationToken);
    }

    public async Task StopMcpClientAsync(McpChatPlugin mcpChatPlugin)
    {
        if (_managedClients.TryRemove(mcpChatPlugin.Id, out var runningClient))
        {
            await runningClient.DisposeAsync();
        }
    }

    public async Task RemoveMcpPluginAsync(McpChatPlugin mcpChatPlugin)
    {
        await StopMcpClientAsync(mcpChatPlugin);
        _mcpPluginsSource.Remove(mcpChatPlugin);
    }

    public async Task<IChatPluginScope> CreateScopeAsync(
        bool isSubagent,
        IReadOnlyDictionary<string, bool>? tools,
        IChatBusyStateIndicator? busyIndicator,
        CancellationToken cancellationToken)
    {
        // Ensure that functions in the scope do not have the same name.
        var functionNameDeduplicator = new HashSet<string>();
        var resultPlugins = new List<ChatPluginSnapshot>();
        IDisposable? startingMcpMessageDisplay = null;

        try
        {
            foreach (var plugin in _builtInPluginsSource.Items
                         .Where(p => !isSubagent || p.IsAllowedInSubagent)
                         .Cast<ChatPlugin>()
                         .Concat(_mcpPluginsSource.Items))
            {
                if (!IsPluginAllowed(plugin)) continue;

                if (plugin is McpChatPlugin mcpChatPlugin)
                {
                    startingMcpMessageDisplay ??=
                        busyIndicator?.SetBusyMessage(new DynamicResourceKey(LocaleKey.ChatContext_BusyMessage_StartingMcp));

                    try
                    {
                        await StartMcpClientAsync(mcpChatPlugin, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        throw new HandledException(
                            ex,
                            new FormattedDynamicResourceKey(
                                LocaleKey.ChatPluginManager_Common_FailedToStartMcpPlugin,
                                new DirectResourceKey(mcpChatPlugin.Name)));
                    }
                }

                var actualFunctions = plugin.GetChatFunctions()
                    .AsValueEnumerable()
                    .Where(f => !isSubagent || f is not BuiltInChatFunction { IsAllowedInSubagent: false })
                    .Where(f => IsFunctionAllowed(plugin, f))
                    .ToList();

                if (actualFunctions.Count > 0 || plugin is McpChatPlugin)
                {
                    resultPlugins.Add(new ChatPluginSnapshot(plugin, functionNameDeduplicator, actualFunctions));
                }
            }

            return new ChatPluginScope(resultPlugins);
        }
        finally
        {
            startingMcpMessageDisplay?.Dispose();
        }


        bool IsPluginAllowed(ChatPlugin plugin)
        {
            var isAllowed = plugin.IsEnabled;
            if (tools == null) return isAllowed;

            foreach (var kvp in tools)
            {
                var dotIndex = kvp.Key.LastIndexOf('.');
                var pluginPattern = dotIndex < 0 ? kvp.Key : kvp.Key[..dotIndex];

                // Use simple Glob to Regex conversion
                var regexPattern = "^" + Regex.Escape(pluginPattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
                var pluginRegex = new Regex(regexPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

                if (pluginRegex.IsMatch(plugin.Key))
                {
                    if (dotIndex < 0)
                    {
                        isAllowed = kvp.Value;
                    }
                    else if (kvp.Value)
                    {
                        // Any rule enabling functions in this plugin forces the plugin to be enabled
                        isAllowed = true;
                    }
                }
            }
            return isAllowed;
        }

        bool IsFunctionAllowed(ChatPlugin plugin, ChatFunction function)
        {
            var isAllowed = function.IsEnabled;
            if (tools == null) return isAllowed;

            var fullFuncName = $"{plugin.Key}.{function.KernelFunction.Metadata.Name}";

            foreach (var kvp in tools)
            {
                var dotIndex = kvp.Key.LastIndexOf('.');
                if (dotIndex < 0)
                {
                    // Rule targets plugin layer
                    var pluginRegexPattern = "^" + Regex.Escape(kvp.Key).Replace("\\*", ".*").Replace("\\?", ".") + "$";
                    var pluginRegex = new Regex(pluginRegexPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

                    // If plugin is explicitly disabled, the function is as well.
                    // Otherwise, we keep its state as is.
                    if (pluginRegex.IsMatch(plugin.Name) && !kvp.Value)
                    {
                        isAllowed = false;
                    }
                }
                else
                {
                    // Rule targets function layer
                    var functionRegexPattern = "^" + Regex.Escape(kvp.Key).Replace("\\*", ".*").Replace("\\?", ".") + "$";
                    var funcRegex = new Regex(functionRegexPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

                    if (funcRegex.IsMatch(fullFuncName))
                    {
                        isAllowed = kvp.Value;
                    }
                }
            }
            return isAllowed;
        }
    }

    private class ChatPluginScope(List<ChatPluginSnapshot> pluginSnapshots) : IChatPluginScope
    {
        public IReadOnlyList<ChatPlugin> Plugins => pluginSnapshots;

        public bool TryGetPluginAndFunction(
            string functionName,
            [NotNullWhen(true)] out ChatPlugin? plugin,
            [NotNullWhen(true)] out ChatFunction? function,
            [NotNullWhen(false)] out IReadOnlyList<string>? similarFunctionNames)
        {
            foreach (var pluginSnapshot in pluginSnapshots)
            {
                if (pluginSnapshot.TryGetChatFunction(functionName, out function))
                {
                    plugin = pluginSnapshot;
                    similarFunctionNames = null;
                    return true;
                }
            }

            plugin = null;
            function = null;
            similarFunctionNames = Process.ExtractTop(
                    functionName,
                    pluginSnapshots.SelectMany(p => p.GetChatFunctions()).Select(f => f.KernelFunction.Name),
                    limit: 5)
                .Where(r => r.Score >= 60)
                .Select(r => r.Value)
                .ToList();
            return false;
        }
    }

    internal void HandleClientDisposed(ManagedMcpClient client)
    {
        _managedClients.TryRemove(client.McpChatPlugin.Id, out _);
    }

    public void Dispose()
    {
        foreach (var mcpClient in _managedClients.Values)
        {
            mcpClient.DisposeAsync().Detach(IExceptionHandler.DangerouslyIgnoreAllException);
        }

        _managedClients.Clear();
        _mcpPluginsSource.Clear();
        _disposables.Dispose();
    }

    /// <summary>
    /// Used to create ILogger instances for MCP clients.
    /// Logs to both the Everywhere logging system and the <see cref="McpChatPlugin"/>'s log entries.
    /// </summary>
    private sealed class McpLoggerFactory(McpChatPlugin mcpChatPlugin, ILoggerFactory innerLoggerFactory) : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider) => innerLoggerFactory.AddProvider(provider);

        public ILogger CreateLogger(string categoryName)
        {
            var innerLogger = innerLoggerFactory.CreateLogger(categoryName);
            return new McpLogger(mcpChatPlugin, innerLogger);
        }

        public void Dispose() => innerLoggerFactory.Dispose();

        private sealed class McpLogger(ILogger mcpChatPlugin, ILogger innerLogger) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => innerLogger.BeginScope(state);

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                mcpChatPlugin.Log(logLevel, eventId, state, exception, formatter);
                innerLogger.Log(logLevel, eventId, state, exception, formatter);
            }
        }
    }

    private class ChatPluginSnapshot : ChatPlugin
    {
        public override string Key => _originalChatPlugin.Key;
        public override IDynamicResourceKey HeaderKey => _originalChatPlugin.HeaderKey;
        public override IDynamicResourceKey DescriptionKey => _originalChatPlugin.DescriptionKey;
        public override LucideIconKind? Icon => _originalChatPlugin.Icon;
        public override string? BeautifulIcon => _originalChatPlugin.BeautifulIcon;
        public override int FunctionCount => _actualFunctions.Count;
        public override ReadOnlyObservableCollection<ChatFunction> Functions => throw new NotSupportedException();

        private readonly ChatPlugin _originalChatPlugin;
        private readonly List<ChatFunction> _actualFunctions;

        public ChatPluginSnapshot(
            ChatPlugin originalChatPlugin,
            HashSet<string> functionNameDeduplicator,
            IReadOnlyList<ChatFunction> actualFunctions) : base(originalChatPlugin.Name)
        {
            _originalChatPlugin = originalChatPlugin;
            AllowedPermissions = originalChatPlugin.AllowedPermissions.ActualValue;
            _actualFunctions = actualFunctions
                .Select(EnsureUniqueFunctionName)
                .ToList();

            ChatFunction EnsureUniqueFunctionName(ChatFunction function)
            {
                var metadata = function.KernelFunction.Metadata;
                if (functionNameDeduplicator.Add(metadata.Name)) return function;

                var postfix = 1;
                string newName;
                do
                {
                    newName = $"{metadata.Name}_{postfix++}";
                }
                while (!functionNameDeduplicator.Add(newName));
                metadata.Name = newName;
                return function;
            }
        }

        public bool TryGetChatFunction(string name, [NotNullWhen(true)] out ChatFunction? function)
        {
            function = _actualFunctions.AsValueEnumerable().FirstOrDefault(f => f.KernelFunction.Metadata.Name == name);
            return function is not null;
        }

        public override bool TryGetFunction(string name, [NotNullWhen(true)] out KernelFunction? function)
        {
            function = _actualFunctions.AsValueEnumerable().Select(f => f.KernelFunction).FirstOrDefault(f => f.Metadata.Name == name);
            return function is not null;
        }

        public override IEnumerator<KernelFunction> GetEnumerator() => _actualFunctions.Select(f => f.KernelFunction).GetEnumerator();

        public override IEnumerable<ChatFunction> GetChatFunctions() => _actualFunctions;

        public override void Dispose() { }
    }
}