using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel.Data;
using Microsoft.SemanticKernel.Plugins.Web;
using Microsoft.SemanticKernel.Plugins.Web.Tavily;

namespace Everywhere.Chat.Plugins;

public partial class WebBrowserPlugin
{
    private class TavilyConnector(string apiKey, HttpClient httpClient, Uri uri, ILoggerFactory? loggerFactory) : IWebSearchEngineConnector
    {
        private readonly TavilyTextSearch _tavilyClient = new(apiKey, new TavilyTextSearchOptions
        {
            HttpClient = httpClient,
            Endpoint = uri,
        });
        private readonly ILogger _logger = loggerFactory?.CreateLogger(typeof(TavilyConnector)) ?? NullLogger.Instance;

        public async Task<IEnumerable<T>> SearchAsync<T>(string query, int count = 1, int offset = 0, CancellationToken cancellationToken = default)
        {
            if (count is < 0 or > 20)
            {
                throw new ArgumentOutOfRangeException(nameof(count), count, $"{nameof(count)} value must be greater than 0 and less than 21.");
            }

            _logger.LogDebug("Sending request");

            var response = await _tavilyClient.GetTextSearchResultsAsync(query, new TextSearchOptions
            {
                Top = count,
            }, cancellationToken);

            _logger.LogDebug("Response received");

            List<T>? returnValues;
            if (typeof(T) == typeof(string))
            {
                returnValues = await response.Results
                    .Take(count)
                    .Select(x => x.Value)
                    .ToListAsync(cancellationToken) as List<T>;
            }
            else if (typeof(T) == typeof(WebPage))
            {
                returnValues = await response.Results
                    .Take(count)
                    .Select(x => new WebPage
                    {
                        Name = x.Name ?? "",
                        Url = x.Link ?? "",
                        Snippet = x.Value
                    })
                    .ToListAsync(cancellationToken) as List<T>;
            }
            else
            {
                throw new NotSupportedException($"Type {typeof(T)} is not supported.");
            }

            return returnValues ?? [];
        }
    }
}