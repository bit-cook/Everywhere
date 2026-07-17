using Everywhere.Chat.Plugins;
using Everywhere.Collections;

namespace Everywhere.Configuration;

public sealed partial class PluginSettings(IServiceProvider serviceProvider) : SettingsBase(serviceProvider)
{
    /// <summary>
    /// Gets or sets whether each plugin is enabled.
    /// </summary>
    /// <remarks>
    /// The key is in the format of "PluginKey.FunctionName".
    /// Plugins are disabled if the key is not present.
    /// But Functions are enabled by default if the plugin is enabled.
    /// </remarks>
    public ObservableDictionary<string, bool> IsEnabledRecords { get; set; } = new();

    /// <summary>
    /// Gets or sets the granted permissions for each plugin function.
    /// The key is in the format of "PluginKey.FunctionName".
    /// </summary>
    public ObservableDictionary<string, bool> IsPermissionGrantedRecords { get; set; } = new();

    /// <summary>
    /// Gets the MCP transport configurations keyed by plugin id.
    /// </summary>
    public ObservableDictionary<Guid, McpTransportConfiguration> McpChatPlugins { get; } = new();

    /// <summary>
    /// Gets or sets the web search engine settings.
    /// </summary>
    public WebSearchEngineSettings WebSearchEngine { get; set; } = new();

    /// <summary>
    /// Gets or sets the web browser settings.
    /// </summary>
    public WebBrowserSettings WebBrowser { get; set; } = new();

    /// <summary>
    /// Gets or sets the terminal settings.
    /// </summary>
    public TerminalPluginSettings Terminal { get; set; } = new();
}