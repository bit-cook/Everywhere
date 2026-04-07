using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel.Plugins.Web;
using ZLinq;

namespace Everywhere.Chat.Plugins.BuiltIn;

public partial class WebBrowserPlugin
{
    private partial class SearxngConnector(HttpClient httpClient, Uri uri, ILoggerFactory? loggerFactory) : IWebSearchEngineConnector
    {
        private readonly ILogger _logger = loggerFactory?.CreateLogger(typeof(SearxngConnector)) ?? NullLogger.Instance;

        public async Task<IEnumerable<T>> SearchAsync<T>(
            string query,
            int count = 1,
            int offset = 0,
            CancellationToken cancellationToken = default)
        {
            if (count is <= 0 or >= 50)
            {
                throw new ArgumentOutOfRangeException(nameof(count), count, $"{nameof(count)} value must be greater than 0 and less than 50.");
            }

            _logger.LogDebug("Sending request: {Uri}", uri);

            using var responseMessage = await httpClient.GetAsync(
                new UriBuilder(uri)
                {
                    Query = $"q={HttpUtility.UrlEncode(query)}&format=json"
                }.Uri,
                cancellationToken).ConfigureAwait(false);

            if (!responseMessage.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"SearXNG API returned error: {responseMessage.StatusCode}");
            }

            _logger.LogDebug("Response received: {StatusCode}", responseMessage.StatusCode);
            var json = await responseMessage.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            // Sensitive data, logging as trace, disabled by default
            _logger.LogTrace("Response content received: {Data}", json);

            var response = JsonSerializer.Deserialize(json, SearxngResponseJsonSerializationContext.Default.SearxngResponse);
            var data = response?.Data;

            if (data is null || data.Length == 0) return [];

            List<T>? returnValues;
            if (typeof(T) == typeof(string))
            {
                returnValues = data
                    .AsValueEnumerable()
                    .Take(count)
                    .Select(x => x.Content)
                    .ToList() as List<T>;
            }
            else if (typeof(T) == typeof(WebPage))
            {
                returnValues = data
                    .AsValueEnumerable()
                    .Take(count)
                    .Select(x => new WebPage
                    {
                        Name = GetFormattedPublishedDate(x) switch
                        {
                            { } date => $"{x.Title} (Published: {date})",
                            _ => x.Title
                        },
                        Url = x.Url,
                        Snippet = x.Content
                    })
                    .ToList() as List<T>;
            }
            else
            {
                throw new NotSupportedException($"Type {typeof(T)} is not supported.");
            }

            return returnValues ?? [];
        }

        private static string? GetFormattedPublishedDate(SearxngSearchResult result)
        {
            return result.PublishedDate1?.ToString("G") ?? result.PublishedDate2?.ToString("G");
        }

        [JsonSerializable(typeof(SearxngResponse))]
        private partial class SearxngResponseJsonSerializationContext : JsonSerializerContext;

        private class SearxngResponse
        {
            [JsonPropertyName("results")]
            public SearxngSearchResult[]? Data { get; init; }
        }

        private sealed class SearxngSearchResult
        {
            /// <summary>
            /// The title of the search result.
            /// </summary>
            [JsonPropertyName("title")]
            public string Title { get; set; } = string.Empty;

            /// <summary>
            /// The URL of the search result.
            /// </summary>
            [JsonPropertyName("url")]
            public string Url { get; set; } = string.Empty;

            /// <summary>
            /// The full content of the search result (if available).
            /// </summary>
            [JsonPropertyName("content")]
            public string Content { get; set; } = string.Empty;

            /// <summary>
            /// The publication date of the search result.
            /// </summary>
            [JsonPropertyName("publishedDate")]
            public DateTime? PublishedDate1 { get; set; }

            [JsonPropertyName("pubDate")]
            public DateTime? PublishedDate2 { get; set; }
        }
    }
}