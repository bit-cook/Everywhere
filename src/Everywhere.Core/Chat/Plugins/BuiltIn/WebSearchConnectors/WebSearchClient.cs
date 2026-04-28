using System.Net.Http.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.SemanticKernel.Data;

namespace Everywhere.Chat.Plugins.BuiltIn;

public interface IWebSearchResponse
{
    IEnumerable<TextSearchResult> ToResults();
}

public abstract class WebSearchClient<TResponse>(HttpClient httpClient, Range validCountRange)
    : IWebSearchEngineConnector where TResponse : class, IWebSearchResponse
{
    protected abstract JsonTypeInfo<TResponse> JsonTypeInfo { get; }

    protected abstract HttpRequestMessage CreateSearchRequest(string query, int count);

    public async Task<IEnumerable<TextSearchResult>> SearchAsync(
        string query,
        int count,
        CancellationToken cancellationToken = default)
    {
        count = Math.Clamp(count, validCountRange.Start.Value, validCountRange.End.Value);

        var requestMessage = CreateSearchRequest(query, count);
        if (requestMessage.Content is not null) await requestMessage.Content.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false);

        using var responseMessage = await httpClient.SendAsync(requestMessage, cancellationToken).ConfigureAwait(false);
        if (!responseMessage.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Web Search API returned error ({responseMessage.ReasonPhrase}) {await TryReadErrorContent(responseMessage, cancellationToken)}",
                null,
                responseMessage.StatusCode);
        }

        var response = await responseMessage.Content.ReadFromJsonAsync(JsonTypeInfo, cancellationToken) ??
            throw new HttpRequestException("Web Search API returned null.", null, responseMessage.StatusCode);

        try
        {
            return response.ToResults().Take(count);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to parse the response from the web search engine.", ex);
        }
    }

    private static async Task<string?> TryReadErrorContent(HttpResponseMessage responseMessage, CancellationToken cancellationToken)
    {
        try
        {
            var content = await responseMessage.Content
                .ReadAsStringAsync(cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(1), cancellationToken)
                .ConfigureAwait(false);
            return content.SafeSubstring(0, 1024);
        }
        catch
        {
            return null;
        }
    }
}