using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Extensions;
using GnomeStack.Os.Secrets;
using Microsoft.Extensions.Logging;

namespace Everywhere.Cloud;

public sealed record UserProfileUpdatedMessage(UserProfile? NewProfile);

public sealed record SubscriptionInformationUpdatedMessage(SubscriptionInformation? NewSubscription);

public partial class OAuthCloudClient : ObservableObject, ICloudClient, IAsyncInitializer, IRecipient<ApplicationCommand>
{
    private const string ServiceName = "com.sylinko.everywhere";
    private const string TokenDataKey = "oauth_token_data";

    private const string AuthorizeEndpoint = $"{CloudConstants.AuthBaseUrl}/api/auth/oauth2/authorize";
    private const string TokenEndpoint = $"{CloudConstants.AuthBaseUrl}/api/auth/oauth2/token";
    private const string UserInfoEndpoint = $"{CloudConstants.AuthBaseUrl}/api/auth/oauth2/userinfo";
    private const string RevokeEndpoint = $"{CloudConstants.AuthBaseUrl}/api/auth/oauth2/revoke";
    private const string SubscriptionEndpoint = $"{CloudConstants.AuthBaseUrl}/api/subscription";
    private const string RedirectUri = "sylinko-everywhere://callback";
    private const string Scopes = "openid profile email offline_access";

    [ObservableProperty]
    public partial UserProfile? UserProfile { get; private set; }

    [ObservableProperty]
    public partial SubscriptionInformation? Subscription { get; private set; }

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OAuthCloudClient> _logger;

    // Core state management extracted into inner contexts
    private readonly TokenSessionContext _session;
    private volatile InteractiveOAuthFlow? _activeAuthFlow;

    // Concurrency control for the UI entry point to prevent multiple login windows
    private readonly SemaphoreSlim _loginLock = new(1, 1);

    public OAuthCloudClient(IHttpClientFactory httpClientFactory, ILogger<OAuthCloudClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        _session = new TokenSessionContext(httpClientFactory, logger);
        WeakReferenceMessenger.Default.Register(this);
    }

    public async Task LoginAsync(CancellationToken cancellationToken)
    {
        // Prevent concurrent login attempts from the UI
        if (!await _loginLock.WaitAsync(0, cancellationToken)) return;

        try
        {
            // 1. Attempt silent login/refresh first using existing session
            if (await _session.GetValidTokenDataAsync(cancellationToken) != null)
            {
                await ReloadUserDataAsync(cancellationToken);
                return;
            }

            // 2. Start interactive OAuth flow
            using var flow = new InteractiveOAuthFlow(_httpClientFactory, cancellationToken);
            _activeAuthFlow = flow; // Expose to the message receiver for URL callbacks

            var authorizeUrl = flow.BuildAuthorizeUrl();
            _logger.LogDebug("Starting login flow. Auth URL: {AuthorizeUrl}", authorizeUrl);
            await App.Launcher.LaunchUriAsync(new Uri(authorizeUrl));

            // Wait for the OS protocol callback or timeout (managed entirely within the flow context)
            var tokenData = await flow.WaitForCodeAndExchangeAsync();

            // 3. Save the new session and fetch user data
            _session.SetToken(tokenData);
            await ReloadUserDataAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("User cancelled the login process or it timed out.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to complete login flow.");
            _session.Clear();
        }
        finally
        {
            _activeAuthFlow = null; // Clean up the transient flow context
            _loginLock.Release();
        }
    }

    public async Task LogoutAsync(CancellationToken cancellationToken)
    {
        if (!await _loginLock.WaitAsync(0, cancellationToken)) return; // Prevent logout during login flow

        try
        {
            await _session.RevokeAndClearAsync(cancellationToken);
            UserProfile = null;
            Subscription = null;
        }
        finally
        {
            _loginLock.Release();
        }
    }

    public async Task ReloadUserDataAsync(CancellationToken cancellationToken)
    {
        // Ensure we have valid tokens, triggering a refresh if necessary.
        // According to BetterAuth docs, /userinfo needs to be authenticated with the Access Token (Opaque), not the ID token (JWT).
        var tokenData = await _session.GetValidTokenDataAsync(cancellationToken);
        var accessToken = tokenData?.AccessToken;

        if (string.IsNullOrEmpty(accessToken))
        {
            throw new UserNotLoginException("Cannot refresh user profile without an access token.");
        }

        using var httpClient = _httpClientFactory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Get, UserInfoEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        UserProfile = await response.Content.ReadFromJsonAsync<UserProfile>(
            UserProfileJsonSerializerContext.Default.Options,
            cancellationToken: cancellationToken);
        WeakReferenceMessenger.Default.Send(new UserProfileUpdatedMessage(UserProfile));

        // The subscription info is not included in the user profile response, so we need a separate call.
        await RefreshSubscriptionAsync(cancellationToken);
    }

    private async Task RefreshSubscriptionAsync(CancellationToken cancellationToken)
    {
        using var httpClient = _httpClientFactory.CreateClient(nameof(ICloudClient));

        var request = new HttpRequestMessage(HttpMethod.Get, SubscriptionEndpoint);
        var response = await httpClient.SendAsync(request, cancellationToken);

        var payload = await ApiPayload<SubscriptionInformation>.EnsureSuccessFromHttpResponseJsonAsync(
            response,
            SubscriptionInformationJsonSerializerContext.Default.ApiPayloadSubscriptionInformation.Options,
            cancellationToken);

        Subscription = payload.EnsureData();
        WeakReferenceMessenger.Default.Send(new SubscriptionInformationUpdatedMessage(Subscription));

        if (Subscription.Plan == SubscriptionPlan.Banned)
        {
            await LogoutAsync(CancellationToken.None); // Enforce ban immediately, cannot be canceled by user
        }
    }

    public DelegatingHandler CreateAuthenticationHandler() => new CloudAuthenticationHandler(_session, _logger);

    public void Receive(ApplicationCommand message)
    {
        if (message is not UrlProtocolCallbackCommand oauth) return;

        _logger.LogDebug("Received URL callback: {url}", oauth.Url);

        // Route the callback to the active interactive flow if one is currently awaiting
        _activeAuthFlow?.HandleCallback(oauth.Url);
    }

    #region IAsyncInitializer Implementation

    public AsyncInitializerIndex Index => AsyncInitializerIndex.Network + 1;

    /// <summary>
    /// Initializes the client by attempting a silent login using stored tokens.
    /// This allows the app to restore the user's session without requiring them to log in again.
    /// </summary>
    public Task InitializeAsync()
    {
        // Fire and forget the initialization to avoid blocking app startup.
        InitializeInternalAsync().Detach();
        return Task.CompletedTask;
    }

    private async Task InitializeInternalAsync()
    {
        if (!await _loginLock.WaitAsync(0)) return; // Prevent reentry for initialization

        try
        {
            _session.LoadFromVault();

            // Try to refresh token and restore user data silently if a refresh token exists
            if (_session.HasRefreshToken)
            {
                var tokenData = await _session.GetValidTokenDataAsync(CancellationToken.None);
                if (tokenData != null)
                {
                    await ReloadUserDataAsync(CancellationToken.None);
                }
            }
        }
        catch (Exception ex)
        {
            // Expected if the user has never logged in or if stored tokens are invalid/expired.
            _logger.LogInformation(ex, "Silent login failed during initialization.");
        }
        finally
        {
            _loginLock.Release();
        }
    }

    #endregion

    /// <summary>
    /// Record for holding token data for secure storage. Using a record for easy JSON serialization and immutability.
    /// </summary>
    private partial record TokenData(
        [property: JsonPropertyName("refresh_token")] string RefreshToken,
        [property: JsonPropertyName("access_token")] string? AccessToken = null,
        [property: JsonPropertyName("id_token")] string? IdToken = null,
        [property: JsonPropertyName("expires_at")] long ExpiresAtTimestamp = 0
    )
    {
        /// <summary>
        /// Checks if the token is expired or will expire within the next 10 seconds.
        /// </summary>
        public bool IsTokenExpiredOrNearingExpiry => ExpiresAtTimestamp < DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 10;

        [JsonSerializable(typeof(TokenData))]
        public partial class TokenDataJsonSerializerContext : JsonSerializerContext;

        public static TokenData? FromJson(string json)
        {
            try
            {
                return JsonSerializer.Deserialize(json, TokenDataJsonSerializerContext.Default.TokenData);
            }
            catch
            {
                return null;
            }
        }

        public string ToJson() => JsonSerializer.Serialize(this, TokenDataJsonSerializerContext.Default.TokenData);
    }

    /// <summary>
    /// Context responsible for the transient, interactive OAuth PKCE flow.
    /// Manages timeouts, state validation, and exchanging the authorization code.
    /// </summary>
    private sealed class InteractiveOAuthFlow : IDisposable
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly string _expectedState;
        private readonly string _codeVerifier;
        private readonly string _codeChallenge;
        private readonly TaskCompletionSource<string> _authCodeTcs;
        private readonly CancellationTokenSource _timeoutCts;
        private readonly CancellationTokenRegistration _ctr;

        public InteractiveOAuthFlow(IHttpClientFactory httpClientFactory, CancellationToken externalToken)
        {
            _httpClientFactory = httpClientFactory;
            _expectedState = Guid.NewGuid().ToString();
            _codeVerifier = GenerateCodeVerifier();
            _codeChallenge = GenerateCodeChallenge(_codeVerifier);
            _authCodeTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            // Limit the wait time for user interaction to 30 minutes to prevent memory leaks
            _timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
            _timeoutCts.CancelAfter(TimeSpan.FromMinutes(30));
            _ctr = _timeoutCts.Token.Register(() => _authCodeTcs.TrySetCanceled());
        }

        public string BuildAuthorizeUrl()
        {
            var sb = new StringBuilder(AuthorizeEndpoint);
            sb.Append("?response_type=code");
            sb.Append($"&client_id={CloudConstants.ClientId}");
            sb.Append($"&redirect_uri={Uri.EscapeDataString(RedirectUri)}");
            sb.Append($"&state={_expectedState}");
            sb.Append($"&scope={Uri.EscapeDataString(Scopes)}");
            sb.Append($"&code_challenge={Uri.EscapeDataString(_codeChallenge)}");
            sb.Append("&code_challenge_method=S256");
            return sb.ToString();
        }

        public void HandleCallback(string url)
        {
            if (_authCodeTcs.Task.IsCompleted) return;

            try
            {
                var uri = new Uri(url);
                var query = HttpUtility.ParseQueryString(uri.Query);
                var code = query["code"];
                var state = query["state"];
                var error = query["error"];
                var errorDesc = query["error_description"];

                if (!string.IsNullOrEmpty(error))
                {
                    _authCodeTcs.TrySetException(new Exception($"OAuth Error: {error} - {errorDesc}"));
                    return;
                }

                if (state != _expectedState)
                {
                    _authCodeTcs.TrySetException(new Exception($"Invalid state received. Expected: {_expectedState}, Received: {state}"));
                    return;
                }

                if (!string.IsNullOrEmpty(code))
                {
                    _authCodeTcs.TrySetResult(code);
                }
                else
                {
                    _authCodeTcs.TrySetException(new Exception("No code found in callback."));
                }
            }
            catch (Exception ex)
            {
                _authCodeTcs.TrySetException(ex);
            }
        }

        public async Task<TokenData> WaitForCodeAndExchangeAsync()
        {
            var code = await _authCodeTcs.Task;

            // Exchange Authorization Code for Access Token
            var parameters = new Dictionary<string, string>
            {
                { "grant_type", "authorization_code" },
                { "code", code },
                { "redirect_uri", RedirectUri },
                { "client_id", CloudConstants.ClientId },
                { "code_verifier", _codeVerifier }
            };

            return await RequestTokenInternalAsync(_httpClientFactory, parameters, _timeoutCts.Token);
        }

        public void Dispose()
        {
            _ctr.Dispose();
            _timeoutCts.Dispose();
        }

        // PKCE Helpers
        private static string GenerateCodeVerifier() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

        private static unsafe string GenerateCodeChallenge(string codeVerifier)
        {
            Span<byte> verifierBytes = stackalloc byte[codeVerifier.Length];
            var bytesWritten = Encoding.ASCII.GetBytes(codeVerifier, verifierBytes);
            verifierBytes = verifierBytes[..bytesWritten];
            Span<byte> hashBytes = stackalloc byte[SHA256.HashSizeInBytes];
            SHA256.HashData(verifierBytes, hashBytes);
            return Base64Url.EncodeToString(hashBytes);
        }
    }

    /// <summary>
    /// Context responsible for long-lived session state, token persistence, and concurrency control for refreshing.
    /// Exposes methods to retrieve valid tokens without bleeding state logic to the main client.
    /// </summary>
    private sealed class TokenSessionContext(IHttpClientFactory httpClientFactory, ILogger logger)
    {
        private TokenData? _tokenData;
        private readonly SemaphoreSlim _refreshLock = new(1, 1);

        public bool HasRefreshToken => _tokenData is { RefreshToken.Length: > 0 };
        public string? CurrentIdToken => _tokenData?.IdToken;

        public void LoadFromVault()
        {
            try
            {
                var json = OsSecretVault.GetSecret(ServiceName, TokenDataKey);
                _tokenData = string.IsNullOrEmpty(json) ? null : TokenData.FromJson(json);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to read token data from secure storage. Proceeding with empty session.");
            }
        }

        public void SetToken(TokenData data)
        {
            _tokenData = data;
            try
            {
                OsSecretVault.SetSecret(ServiceName, TokenDataKey, data.ToJson());
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to save token data to secure storage");
            }
        }

        public void Clear()
        {
            _tokenData = null;
            try
            {
                OsSecretVault.DeleteSecret(ServiceName, TokenDataKey);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to delete token data from secure storage");
            }
        }

        /// <summary>
        /// Ensures the current token data is valid. If it's nearing expiry, automatically triggers a refresh.
        /// Returns the latest TokenData object, allowing callers to extract IdToken or AccessToken as needed.
        /// </summary>
        public async Task<TokenData?> GetValidTokenDataAsync(CancellationToken cancellationToken)
        {
            if (_tokenData is null) return null;

            // If token is still fresh, return it directly
            if (!_tokenData.IsTokenExpiredOrNearingExpiry) return _tokenData;

            // If it's expired or nearing expiry, attempt to refresh it
            var refreshed = await TryRefreshAsync(cancellationToken);
            return refreshed ? _tokenData : null;
        }

        public async Task<bool> TryRefreshAsync(CancellationToken cancellationToken)
        {
            if (!HasRefreshToken) return false;

            // Prevent concurrent refresh attempts (Double-check locking pattern)
            if (!await _refreshLock.WaitAsync(0, cancellationToken))
            {
                await _refreshLock.WaitAsync(cancellationToken);
                _refreshLock.Release();

                // Another thread might have completed the refresh while we were waiting
                return _tokenData is { IsTokenExpiredOrNearingExpiry: false };
            }

            try
            {
                var parameters = new Dictionary<string, string>
                {
                    { "grant_type", "refresh_token" },
                    { "refresh_token", _tokenData!.RefreshToken },
                    { "client_id", CloudConstants.ClientId }
                };

                var newTokenData = await RequestTokenInternalAsync(httpClientFactory, parameters, cancellationToken);
                SetToken(newTokenData);
                return true;
            }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
            {
                // Refresh token is explicitly rejected by the server (e.g., revoked, expired)
                logger.LogWarning(ex, "Refresh token rejected by server. Clearing local session.");
                Clear();
                return false;
            }
            catch (Exception ex)
            {
                // Network errors or other transient issues. Keep the current session, it might recover later.
                logger.LogWarning(ex, "Token refresh failed due to network or unknown error.");
                return false;
            }
            finally
            {
                _refreshLock.Release();
            }
        }

        public async Task RevokeAndClearAsync(CancellationToken cancellationToken)
        {
            if (_tokenData is null) return;

            using var httpClient = httpClientFactory.CreateClient();

            var accessToken = _tokenData.AccessToken;
            var refreshToken = _tokenData.RefreshToken;

            Clear(); // Clear locally first to ensure UI reflects logout immediately

            if (!string.IsNullOrEmpty(accessToken)) await RevokeTokenInternalAsync(httpClient, accessToken, "access_token", cancellationToken);
            if (!string.IsNullOrEmpty(refreshToken)) await RevokeTokenInternalAsync(httpClient, refreshToken, "refresh_token", cancellationToken);
        }

        private async Task RevokeTokenInternalAsync(HttpClient client, string token, string tokenTypeHint, CancellationToken cancellationToken)
        {
            try
            {
                var parameters = new Dictionary<string, string>
                {
                    { "token", token },
                    { "token_type_hint", tokenTypeHint },
                    { "client_id", CloudConstants.ClientId }
                };
                var request = new HttpRequestMessage(HttpMethod.Post, RevokeEndpoint) { Content = new FormUrlEncodedContent(parameters) };
                await client.SendAsync(request, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to revoke remote {TokenType}", tokenTypeHint);
            }
        }
    }

    /// <summary>
    /// Shared HTTP logic for requesting tokens (used by both initial exchange and refresh).
    /// </summary>
    private static async Task<TokenData> RequestTokenInternalAsync(IHttpClientFactory factory, Dictionary<string, string> parameters, CancellationToken ct)
    {
        using var httpClient = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(parameters)
        };

        var response = await httpClient.SendAsync(request, ct);
        var content = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();

        var tokenData = TokenData.FromJson(content);
        return tokenData ?? throw new HttpRequestException("Failed to parse token response. Invalid format.", null, response.StatusCode);
    }

    /// <summary>
    /// A delegating handler that automatically adds JWT authentication headers to outgoing requests
    /// and handles token refresh on 401 Unauthorized responses.
    /// </summary>
    /// <remarks>
    /// This handler MUST use the IdToken (JWT) for general API requests, as resource servers validate claims.
    /// </remarks>
    private sealed class CloudAuthenticationHandler(TokenSessionContext session, ILogger logger) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // 1. Proactively get a valid token data and extract the IdToken (JWT)
            var tokenData = await session.GetValidTokenDataAsync(cancellationToken);
            var idToken = tokenData?.IdToken;

            if (string.IsNullOrEmpty(idToken))
            {
                throw new UserNotLoginException();
            }

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", idToken);
            request.Headers.Add("ew-device-id", RuntimeConstants.DeviceId);

            // Ensure content is buffered for potential retry
            if (request.Content != null)
            {
                await request.Content.LoadIntoBufferAsync(cancellationToken);
            }

            var response = await base.SendAsync(request, cancellationToken);

            // If the response is not 401, return it as is
            if (response.StatusCode != HttpStatusCode.Unauthorized) return response;

            logger.LogDebug("Received 401 Unauthorized, attempting to force refresh token...");

            // 2. If we get a 401, the server might have invalidated the token early. Force a refresh.
            var refreshed = await session.TryRefreshAsync(cancellationToken);
            if (!refreshed) return response;

            // 3. Extract the new IdToken after successful refresh
            var newIdToken = session.CurrentIdToken;
            if (string.IsNullOrEmpty(newIdToken)) return response;

            // Dispose the original response before retrying
            response.Dispose();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", newIdToken);
            return await base.SendAsync(request, cancellationToken);
        }
    }
}