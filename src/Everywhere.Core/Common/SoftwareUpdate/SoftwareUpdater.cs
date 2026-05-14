using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Configuration;
using Everywhere.Interop;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
#if !DEBUG
using Everywhere.Utilities;
#endif

namespace Everywhere.Common;

public sealed partial class SoftwareUpdater(
    IPlatformUpdateHandler platformHandler,
    IHttpClientFactory httpClientFactory,
    INativeHelper nativeHelper,
    ILogger<SoftwareUpdater> logger
) : ObservableObject, ISoftwareUpdater, IDisposable
{
    private const string CustomUpdateServiceBaseUrl = "https://download.sylinko.com";
    private const string ApiUrl = $"{CustomUpdateServiceBaseUrl}/api?product=everywhere";
    private readonly string _downloadUrlBase = $"{CustomUpdateServiceBaseUrl}/download?product=everywhere&os={platformHandler.OsIdentifier}";
    private const string GitHubDirectUrlBase = "https://github.com/Sylinko/Everywhere/releases/download";

    /// <summary>
    /// Minimum age of a release before auto-download is triggered (24 hours).
    /// Prevents automatic distribution of releases that may have critical regressions discovered shortly after publish.
    /// </summary>
    private static readonly TimeSpan MinReleaseAgeForAutoDownload = TimeSpan.FromHours(24);

    /// <summary>
    /// Auto-download bandwidth cap in bytes per second (1 MB/s).
    /// </summary>
    private const long AutoDownloadBytesPerSecond = 1024 * 1024;

    /// <summary>
    /// Maximum number of consecutive auto-download retries before waiting for the next check cycle.
    /// </summary>
    private const int MaxAutoDownloadRetries = 3;

    private readonly ActivitySource _activitySource = new(typeof(SoftwareUpdater).FullName.NotNull());

#if !DEBUG
    private PeriodicTimer? _timer;
#endif

    private Task? _updateTask;
    private Task? _autoDownloadTask;
    private int _autoDownloadRetryCount;

    public Version CurrentVersion { get; } = typeof(SoftwareUpdater).Assembly.GetName().Version ?? new Version(0, 0, 0);

    [ObservableProperty]
    public partial DateTimeOffset? LastCheckTime { get; private set; }

    [ObservableProperty]
    public partial SoftwareUpdateMetadata? LatestUpdate { get; private set; }

    [ObservableProperty]
    public partial bool IsDownloading { get; private set; }

    public void RunAutomaticCheckInBackground(TimeSpan interval, CancellationToken cancellationToken = default)
    {
#if !DEBUG
        PeriodicTimer timer;
        _timer = timer = new PeriodicTimer(interval);
        cancellationToken.Register(Stop);

        Task.Run(
            async () =>
            {
                // Clean up old update packages on startup.
                CleanupOldUpdates();
                await CheckForUpdatesAsync(false, cancellationToken); // check immediately on start

                try
                {
                    while (await timer.WaitForNextTickAsync(cancellationToken))
                    {
                        await CheckForUpdatesAsync(false, cancellationToken);
                    }
                }
                catch (OperationCanceledException) { }
            },
            cancellationToken);

        void Stop()
        {
            DisposeCollector.DisposeToDefault(ref _timer);
        }
#endif
    }

    public async Task CheckForUpdatesAsync(bool throwOnError, CancellationToken cancellationToken = default)
    {
        try
        {
            if (_updateTask is not null) return;

            using var httpClient = httpClientFactory.CreateClient(Options.DefaultName);
            var response = await httpClient.GetAsync(ApiUrl, cancellationToken);
            response.EnsureSuccessStatusCode();

            var jsonDoc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = jsonDoc.RootElement;

            var latestTag = root.GetProperty("tag_name").GetString();
            if (latestTag is null) return;

            var versionString = latestTag.StartsWith('v') ? latestTag[1..] : latestTag;
            if (!Version.TryParse(versionString, out var latestVersion))
            {
                logger.LogWarning("Could not parse version from tag: {Tag}", latestTag);
                return;
            }

            // Only proceed if the remote version is actually newer.
            if (latestVersion <= CurrentVersion)
            {
                LatestUpdate = null;
                return;
            }

            // Parse release metadata.
            var publishedAt = root.TryGetProperty("published_at", out var publishedAtElement) &&
                publishedAtElement.TryGetDateTimeOffset(out var date) ?
                    date :
                    DateTimeOffset.UtcNow;
            var releaseNotes = root.TryGetProperty("body", out var bodyElement) ? bodyElement.GetString() : null;
            var assets = root.GetProperty("assets").Deserialize(UpdateAssetMetadataJsonSerializerContext.Default.ListUpdateAssetMetadata);

            SoftwareUpdateMetadata.UpdateAsset? asset = null;
            var assetMetadata = platformHandler.SelectAsset(assets ?? [], versionString);
            if (assetMetadata is not null)
            {
                asset = new SoftwareUpdateMetadata.UpdateAsset(
                    assetMetadata.Name,
                    assetMetadata.Digest,
                    assetMetadata.Size,
                    $"{_downloadUrlBase}&type={platformHandler.GetDownloadType()}",
                    $"{GitHubDirectUrlBase}/{latestTag}/{assetMetadata.Name}"
                );
            }

            LatestUpdate = new SoftwareUpdateMetadata(latestVersion, publishedAt, releaseNotes, asset);

            // Trigger background auto-download if conditions are met.
            TriggerAutoDownloadIfAppropriate();
        }
        catch (Exception ex)
        {
            ex = HandledSystemException.Handle(ex);
            logger.LogWarning(ex, "Failed to check for updates.");
            LatestUpdate = null;

            if (throwOnError) throw;
        }
        finally
        {
            LastCheckTime = DateTimeOffset.UtcNow;
        }
    }

    public async Task PerformUpdateAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (_updateTask is not null)
        {
            await _updateTask;
            return;
        }

        if (LatestUpdate is not { Asset: { } asset })
        {
            logger.LogDebug("No new version available to update.");
            return;
        }

        _updateTask = Task.Run(
            async () =>
            {
                using var activity = _activitySource.StartActivity();

                try
                {
                    var assetPath = await DownloadAssetAsync(asset, progress, cancellationToken);
                    await platformHandler.ExecuteUpdateAsync(assetPath, cancellationToken);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    ex = new HandledException(ex, new DynamicResourceKey(LocaleKey.SoftwareUpdater_PerformUpdate_FailedToast_Message));
                    activity?.SetStatus(ActivityStatusCode.Error, ex.Message);

                    logger.LogError(ex, "Failed to perform update.");
                    throw;
                }
                finally
                {
                    _updateTask = null;
                }
            },
            cancellationToken);

        await _updateTask;
    }

#if !DEBUG
    /// <summary>
    /// Cleans up old update packages from the updates directory.
    /// </summary>
    private void CleanupOldUpdates()
    {
        try
        {
            var updatesPath = RuntimeConstants.EnsureWritableDataFolderPath("updates");
            if (!Directory.Exists(updatesPath)) return;

            foreach (var file in Directory.EnumerateFiles(updatesPath))
            {
                var fileName = Path.GetFileName(file);

                if (!platformHandler.TryParseUpdatePackageVersion(fileName, out var fileVersion) || fileVersion is null) continue;

                // Delete if the package version is older than or same as the current running version.
                if (fileVersion > CurrentVersion) continue;

                try
                {
                    File.Delete(file);
                    logger.LogDebug("Deleted old update package: {FileName}", fileName);
                }
                catch (Exception ex)
                {
                    ex = HandledSystemException.Handle(ex);
                    logger.LogInformation(ex, "Failed to delete old update package: {FileName}", fileName);
                }
            }
        }
        catch (Exception ex)
        {
            ex = HandledSystemException.Handle(ex);
            logger.LogInformation(ex, "An error occurred during old updates cleanup.");
        }
    }
#endif

    /// <summary>
    /// Triggers automatic background download if: not in low-data mode, release is old enough,
    /// and no download is already in progress or has exceeded max retries.
    /// </summary>
    private void TriggerAutoDownloadIfAppropriate()
    {
        if (LatestUpdate is not { Asset: { } asset }) return;
        if (nativeHelper.IsLowDataModeActive) return;

        var releaseAge = DateTimeOffset.UtcNow - LatestUpdate.PublishedAt;
        if (releaseAge < MinReleaseAgeForAutoDownload)
        {
            logger.LogDebug(
                "Auto-download skipped: release is too recent (age: {Age:h\\:mm}, min: {Min:h\\:mm}).",
                releaseAge,
                MinReleaseAgeForAutoDownload);
            return;
        }

        if (_autoDownloadTask is { IsCompleted: false })
        {
            logger.LogDebug("Auto-download already in progress.");
            return;
        }

        if (_autoDownloadRetryCount >= MaxAutoDownloadRetries)
        {
            logger.LogDebug("Auto-download retry limit reached ({Count}).", _autoDownloadRetryCount);
            return;
        }

        _autoDownloadTask = Task.Run(() => AutoDownloadWithRetryAsync(asset));
        _autoDownloadTask.Detach(IExceptionHandler.DangerouslyIgnoreAllException);
    }

    /// <summary>
    /// Performs the auto-download with exponential backoff on failure.
    /// </summary>
    private async Task AutoDownloadWithRetryAsync(SoftwareUpdateMetadata.UpdateAsset asset)
    {
        try
        {
            IsDownloading = true;
            var progress = new Progress<double>(p => LatestUpdate?.DownloadProgress = p);

            await DownloadAssetAsync(asset, progress, CancellationToken.None);
            _autoDownloadRetryCount = 0;
            LatestUpdate?.IsReady = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ex = HandledSystemException.Handle(ex);
            logger.LogWarning(ex, "Auto-download attempt {Attempt} failed.", _autoDownloadRetryCount + 1);

            _autoDownloadRetryCount++;

            if (_autoDownloadRetryCount < MaxAutoDownloadRetries)
            {
                // Exponential backoff: 1h, 2h, 4h.
                var delay = TimeSpan.FromHours(Math.Pow(2, _autoDownloadRetryCount - 1));
                logger.LogInformation("Retrying auto-download in {Delay}.", delay);
                await Task.Delay(delay);

                // Only retry if we still have the same update target.
                if (LatestUpdate?.Asset is not null)
                {
                    TriggerAutoDownloadIfAppropriate();
                }
            }
        }
        finally
        {
            IsDownloading = false;
        }
    }

    private async Task<string> DownloadAssetAsync(
        SoftwareUpdateMetadata.UpdateAsset asset,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var installPath = RuntimeConstants.EnsureWritableDataFolderPath("updates");
        var assetDownloadPath = Path.Combine(installPath, asset.Name);

        var fileInfo = new FileInfo(assetDownloadPath);
        if (fileInfo.Exists)
        {
            if (fileInfo.Length == asset.Size && string.Equals(await HashFileAsync(), asset.Digest, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation("Asset {AssetName} already exists and is valid, skipping download.", asset.Name);
                progress?.Report(1.0);
                return assetDownloadPath;
            }

            // If the existing file is larger than expected (corrupt), delete and restart.
            if (fileInfo.Length > asset.Size)
            {
                logger.LogInformation("Asset {AssetName} is larger than expected, redownloading.", asset.Name);
                fileInfo.Delete();
            }
            else
            {
                logger.LogInformation(
                    "Asset {AssetName} partial download detected ({Existing}/{Total} bytes), resuming.",
                    asset.Name,
                    fileInfo.Length,
                    asset.Size);
            }
        }

        using var httpClient = httpClientFactory.CreateClient(Options.DefaultName);
        var resumeOffset = fileInfo.Exists ? fileInfo.Length : 0L;
        var url = resumeOffset > 0 ? asset.DirectDownloadUrl : await SelectDownloadUrlAsync(httpClient, asset, cancellationToken);

        // If resuming, prefer the direct URL; fall back to proxy on failure.
        if (resumeOffset > 0)
        {
            try
            {
                return await DownloadWithResumeAsync(httpClient, url, assetDownloadPath, asset, resumeOffset, progress, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var handledEx = HandledSystemException.Handle(ex);
                logger.LogWarning(handledEx, "Resume download failed, restarting from proxy.");
                fileInfo.Delete();
                resumeOffset = 0;
                url = asset.ProxyDownloadUrl;
            }
        }

        return await DownloadWithResumeAsync(httpClient, url, assetDownloadPath, asset, resumeOffset, progress, cancellationToken);

        async Task<string> HashFileAsync()
        {
            await using var fileStream = new FileStream(fileInfo.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
            var sha256 = await SHA256.HashDataAsync(fileStream, cancellationToken);
            return "sha256:" + Convert.ToHexString(sha256);
        }
    }

    /// <summary>
    /// Selects the best download URL: direct GitHub with a 3-second timeout, falling back to the proxy.
    /// </summary>
    private async Task<string> SelectDownloadUrlAsync(
        HttpClient httpClient,
        SoftwareUpdateMetadata.UpdateAsset asset,
        CancellationToken cancellationToken)
    {
        try
        {
            var directPingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            logger.LogInformation("Attempting to download update direct via GitHub: {Url}", asset.DirectDownloadUrl);

            var fetchTask = httpClient.GetAsync(asset.DirectDownloadUrl, HttpCompletionOption.ResponseHeadersRead, directPingCts.Token);
            var completedTask = await Task.WhenAny(fetchTask, Task.Delay(3000, cancellationToken));

            if (completedTask == fetchTask && fetchTask.Status == TaskStatus.RanToCompletion)
            {
                fetchTask.Result.EnsureSuccessStatusCode();
                fetchTask.Result.Dispose();
                logger.LogInformation("Direct download is responsive, proceeding to download asset.");
                return asset.DirectDownloadUrl;
            }

            await directPingCts.CancelAsync();
            if (fetchTask.IsFaulted)
            {
                logger.LogWarning(fetchTask.Exception, "GitHub direct download failed. Falling back to ghproxy.");
            }
            else
            {
                logger.LogWarning("GitHub direct download timed out. Falling back to ghproxy.");
            }
        }
        catch (Exception ex)
        {
            ex = HandledSystemException.Handle(ex);
            logger.LogInformation(ex, "Failed during download selection. Falling back to ghproxy.");
        }

        return asset.ProxyDownloadUrl;
    }

    /// <summary>
    /// Downloads the asset with HTTP Range support and token-bucket bandwidth throttling.
    /// </summary>
    private async Task<string> DownloadWithResumeAsync(
        HttpClient httpClient,
        string url,
        string downloadPath,
        SoftwareUpdateMetadata.UpdateAsset asset,
        long resumeOffset,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (resumeOffset > 0)
        {
            request.Headers.Range = new RangeHeaderValue(resumeOffset, null);
            logger.LogInformation("Requesting range: bytes={Offset}-", resumeOffset);
        }

        var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (resumeOffset > 0 && response.StatusCode == HttpStatusCode.OK)
        {
            // Server ignored the Range header; we must restart from scratch.
            response.Dispose();
            request.Dispose();
            logger.LogWarning("Server did not honor Range request, restarting download.");
            File.Delete(downloadPath);
            resumeOffset = 0;
            request = new HttpRequestMessage(HttpMethod.Get, url);
            response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }

        response.EnsureSuccessStatusCode();

        {
            var totalBytes = (response.Content.Headers.ContentLength ?? (asset.Size - resumeOffset)) + resumeOffset;
            var fileMode = resumeOffset > 0 ? FileMode.Append : FileMode.Create;
            await using var writeStream = new FileStream(downloadPath, fileMode, FileAccess.Write, FileShare.None);
            writeStream.Seek(resumeOffset, SeekOrigin.Begin);

            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);

            // Wrap with token-bucket rate limiter for smooth, non-bursty throttling.
            var rateLimiter = new TokenBucketRateLimiter(AutoDownloadBytesPerSecond);
            var totalBytesRead = resumeOffset;
            var buffer = new byte[81920];

            while (totalBytesRead < totalBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var requested = (int)Math.Min(buffer.Length, totalBytes - totalBytesRead);
                var allowed = await rateLimiter.AcquireAsync(requested, cancellationToken);

                var bytesRead = await contentStream.ReadAsync(buffer.AsMemory(0, allowed), cancellationToken);
                if (bytesRead == 0) break;

                await writeStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                totalBytesRead += bytesRead;

                // Return unused tokens if the stream returned fewer bytes than we acquired.
                if (bytesRead < allowed)
                {
                    rateLimiter.ReturnUnused(allowed - bytesRead);
                }

                progress?.Report((double)totalBytesRead / totalBytes);
            }

            response.Dispose();

            if (totalBytesRead < totalBytes)
            {
                throw new InvalidOperationException(
                    $"Download incomplete: received {totalBytesRead} of {totalBytes} bytes for {asset.Name}.");
            }
        }

        {
            await using var readStream = new FileStream(downloadPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var sha256 = await SHA256.HashDataAsync(readStream, cancellationToken);
            if (!string.Equals(
                    "sha256:" + Convert.ToHexString(sha256),
                    asset.Digest,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Downloaded asset {asset.Name} hash does not match expected digest.");
            }
        }

        return downloadPath;
    }

    public void Dispose()
    {
#if !DEBUG
        _timer?.Dispose();
#endif
    }
}