using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel.Plugins.Web;
using ZLinq;

namespace Everywhere.Chat.Plugins.BuiltIn;

public partial class WebBrowserPlugin
{
    private partial class JinaConnector : IWebSearchEngineConnector
    {
        private readonly ILogger _logger;
        private readonly HttpClient _httpClient;
        private readonly Uri _uri;

        /// <summary>
        /// Initializes a new instance of the <see cref="JinaConnector"/> class.
        /// </summary>
        /// <param name="apiKey">The API key to authenticate the connector.</param>
        /// <param name="httpClient"></param>
        /// <param name="uri">The URI of the Jina Search instance. Defaults to "https://s.jina.ai/".</param>
        /// <param name="loggerFactory">The <see cref="ILoggerFactory"/> to use for logging. If null, no logging will be performed.</param>
        public JinaConnector(string apiKey, HttpClient httpClient, Uri uri, ILoggerFactory? loggerFactory)
        {
            _httpClient = httpClient;
            _logger = loggerFactory?.CreateLogger(typeof(JinaConnector)) ?? NullLogger.Instance;
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            _uri = uri;
        }

        /// <inheritdoc/>
        public async Task<IEnumerable<T>> SearchAsync<T>(string query, int count = 1, int offset = 0, CancellationToken cancellationToken = default)
        {
            if (count is <= 0 or >= 50)
            {
                throw new ArgumentOutOfRangeException(nameof(count), count, $"{nameof(count)} value must be greater than 0 and less than 50.");
            }

            _logger.LogDebug("Sending request: {Uri}", _uri);

            using var responseMessage = await _httpClient.PostAsync(
                _uri,
                JsonContent.Create(
                    new
                    {
                        q = query,
                        num = count,
                        page = offset
                    }),
                cancellationToken).ConfigureAwait(false);

            _logger.LogDebug("Response received: {StatusCode}", responseMessage.StatusCode);

            var json = await responseMessage.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            // Sensitive data, logging as trace, disabled by default
            _logger.LogTrace("Response content received: {Data}", json);

            var response = JsonSerializer.Deserialize(json, JinaResponseJsonSerializationContext.Default.JinaResponse);
            if (response is not { Code: 200, Data: { } data })
            {
                throw new HttpRequestException($"Jina API returned error: {response?.Code}");
            }

            if (data is null || data.Length == 0) return [];

            List<T>? returnValues;
            if (typeof(T) == typeof(string))
            {
                returnValues = data
                    .AsValueEnumerable()
                    .Take(count)
                    .Select(x => x.Content ?? x.Description)
                    .ToList() as List<T>;
            }
            else if (typeof(T) == typeof(WebPage))
            {
                returnValues = data
                    .AsValueEnumerable()
                    .Take(count)
                    .Select(x => new WebPage
                    {
                        Name = x.Title,
                        Url = x.Url,
                        Snippet = x.Description
                    })
                    .ToList() as List<T>;
            }
            else
            {
                throw new NotSupportedException($"Type {typeof(T)} is not supported.");
            }

            return returnValues ?? [];
        }

        [JsonSerializable(typeof(JinaResponse))]
        private partial class JinaResponseJsonSerializationContext : JsonSerializerContext;

        private class JinaResponse
        {
            [JsonPropertyName("code")]
            public int Code { get; init; }

            [JsonPropertyName("status")]
            public int? Status { get; init; }

            [JsonPropertyName("data")]
            public JinaSearchResult[]? Data { get; init; }
        }

        private sealed class JinaSearchResult
        {
            /// <summary>
            /// The title of the search result.
            /// </summary>
            [JsonPropertyName("title")]
            public string Title { get; set; } = string.Empty;

            /// <summary>
            /// The description/snippet of the search result.
            /// </summary>
            [JsonPropertyName("description")]
            public string Description { get; set; } = string.Empty;

            /// <summary>
            /// The URL of the search result.
            /// </summary>
            [JsonPropertyName("url")]
            public string Url { get; set; } = string.Empty;

            /// <summary>
            /// The full content of the search result (if available).
            /// </summary>
            [JsonPropertyName("content")]
            public string? Content { get; set; }
        }
    }
}