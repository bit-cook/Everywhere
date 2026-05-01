using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Everywhere.Configuration;
using Everywhere.Interop;
using Everywhere.Messages;
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
    private const string CustomUpdateServiceBaseUrl = "https://ghproxy.sylinko.com";
    private const string ApiUrl = $"{CustomUpdateServiceBaseUrl}/api?product=everywhere";
    private readonly string _downloadUrlBase = $"{CustomUpdateServiceBaseUrl}/download?product=everywhere&os={platformHandler.OsIdentifier}";
    private const string GitHubDirectUrlBase = "https://github.com/DearVa/Everywhere/releases/download";

    private readonly ActivitySource _activitySource = new(typeof(SoftwareUpdater).FullName.NotNull());

#if !DEBUG
    private PeriodicTimer? _timer;
#endif

    private Task? _updateTask;
    private Asset? _latestAsset;
    private Version? _notifiedVersion;

    public Version CurrentVersion { get; } = typeof(SoftwareUpdater).Assembly.GetName().Version ?? new Version(0, 0, 0);

    [ObservableProperty] public partial DateTimeOffset? LastCheckTime { get; private set; }

    [ObservableProperty] public partial Version? LatestVersion { get; private set; }

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

            var assets = root.GetProperty("assets").Deserialize(UpdateAssetMetadataJsonSerializerContext.Default.ListUpdateAssetMetadata);
            if (assets is null) return;

            var assetMetadata = platformHandler.SelectAsset(assets, versionString);
            if (assetMetadata is not null)
            {
                _latestAsset = new Asset(
                    assetMetadata.Name,
                    assetMetadata.Digest,
                    assetMetadata.Size,
                    $"{_downloadUrlBase}&type={platformHandler.GetDownloadType()}",
                    $"{GitHubDirectUrlBase}/{latestTag}/{assetMetadata.Name}"
                );
            }

            LatestVersion = latestVersion > CurrentVersion ? latestVersion : null;

            if (_notifiedVersion != LatestVersion && LatestVersion is not null)
            {
                _notifiedVersion = LatestVersion;
                nativeHelper.ShowDesktopNotificationAsync(
                        new FormattedDynamicResourceKey(
                            LocaleKey.SoftwareUpdater_UpdateAvailable_Toast_Message,
                            new DirectResourceKey(CurrentVersion.ToString()),
                            new DirectResourceKey(LatestVersion.ToString())).ToString(),
                        LocaleResolver.Common_Info)
                    .ContinueWith(
                        t =>
                        {
                            if (t is { IsCompletedSuccessfully: true, Result: true })
                            {
                                WeakReferenceMessenger.Default.Send<ApplicationMessage>(new ShowWindowMessage(ShowWindowMessage.MainWindow));
                            }
                        },
                        CancellationToken.None)
                    .Detach(IExceptionHandler.DangerouslyIgnoreAllException);
            }
        }
        catch (Exception ex)
        {
            ex = HandledSystemException.Handle(ex);
            logger.LogWarning(ex, "Failed to check for updates.");
            LatestVersion = null;

            if (throwOnError) throw;
        }
        finally
        {
            LastCheckTime = DateTimeOffset.UtcNow;
        }
    }

    public async Task PerformUpdateAsync(IProgress<double> progress, CancellationToken cancellationToken = default)
    {
        if (_updateTask is not null)
        {
            await _updateTask;
            return;
        }

        if (LatestVersion is null || LatestVersion <= CurrentVersion || _latestAsset is not { } asset)
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

    private async Task<string> DownloadAssetAsync(Asset asset, IProgress<double> progress, CancellationToken cancellationToken = default)
    {
        var installPath = RuntimeConstants.EnsureWritableDataFolderPath("updates");
        var assetDownloadPath = Path.Combine(installPath, asset.Name);

        var fileInfo = new FileInfo(assetDownloadPath);
        if (fileInfo.Exists)
        {
            if (fileInfo.Length == asset.Size && string.Equals(await HashFileAsync(), asset.Digest, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation("Asset {AssetName} already exists and is valid, skipping download.", asset.Name);
                progress.Report(1.0);
                return assetDownloadPath;
            }

            logger.LogInformation("Asset {AssetName} exists but is invalid, redownloading.", asset.Name);
            fileInfo.Delete();
        }

        using var httpClient = httpClientFactory.CreateClient(Options.DefaultName);
        HttpResponseMessage? response = null;

        try
        {
            var directPingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            logger.LogInformation("Attempting to download update direct via GitHub: {Url}", asset.DirectDownloadUrl);

            var fetchTask = httpClient.GetAsync(asset.DirectDownloadUrl, HttpCompletionOption.ResponseHeadersRead, directPingCts.Token);
            var completedTask = await Task.WhenAny(fetchTask, Task.Delay(3000, cancellationToken));

            if (completedTask == fetchTask && fetchTask.Status == TaskStatus.RanToCompletion)
            {
                response = await fetchTask;
                response.EnsureSuccessStatusCode();
                logger.LogInformation("Direct download is responsive, proceeding to download asset.");
            }
            else
            {
                await directPingCts.CancelAsync();
                if (fetchTask.IsFaulted)
                {
                    logger.LogWarning(fetchTask.Exception, "GitHub direct download failed. Falling back to ghproxy.");
                }
                else
                {
                    logger.LogWarning("GitHub direct download timed out. Falling back to ghproxy.");
                }
                response = await httpClient.GetAsync(asset.ProxyDownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
            }
        }
        catch (Exception ex)
        {
            ex = HandledSystemException.Handle(ex);
            logger.LogInformation(ex, "Failed during download selection. Falling back to ghproxy: {Url}", asset.ProxyDownloadUrl);

            response?.Dispose();
            response = await httpClient.GetAsync(asset.ProxyDownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
        }

        await using var fs = new FileStream(assetDownloadPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);

        var totalBytes = response.Content.Headers.ContentLength ?? asset.Size;
        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var totalBytesRead = 0L;
        var buffer = new byte[81920];
        int bytesRead;

        while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await fs.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            totalBytesRead += bytesRead;
            progress.Report((double)totalBytesRead / totalBytes);
        }

        response.Dispose();

        fs.Position = 0;
        if (!string.Equals(
                "sha256:" + Convert.ToHexString(await SHA256.HashDataAsync(fs, cancellationToken)),
                asset.Digest,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Downloaded asset {asset.Name} hash does not match expected digest.");
        }

        return assetDownloadPath;

        async Task<string> HashFileAsync()
        {
            await using var fileStream = new FileStream(fileInfo.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
            var sha256 = await SHA256.HashDataAsync(fileStream, cancellationToken);
            return "sha256:" + Convert.ToHexString(sha256);
        }
    }

    public void Dispose()
    {
#if !DEBUG
        _timer?.Dispose();
#endif
    }

    private record Asset(
        string Name,
        string Digest,
        long Size,
        string ProxyDownloadUrl,
        string DirectDownloadUrl
    );
}