using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace ALyricEase.Services;

/// <summary>记录更新源的实际结果，保留 GithubSource 原有的筛选和异常语义。</summary>
internal sealed class LoggingGithubSource(string repoUrl, ILogger logger, IFileDownloader? downloader = null)
    : GithubSource(repoUrl, null, false, downloader)
{
    protected override async Task<GithubRelease[]> GetReleases(bool includePrereleases)
    {
        var timer = Stopwatch.StartNew();
        logger.LogInformation("GitHub release list request started. IncludePrereleases={IncludePrereleases}", includePrereleases);
        try
        {
            var releases = await base.GetReleases(includePrereleases).ConfigureAwait(false);
            logger.LogInformation("GitHub release list received. ReleaseCount={ReleaseCount} ElapsedMs={ElapsedMs}",
                releases.Length, timer.ElapsedMilliseconds);
            return releases;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GitHub release list request failed. ElapsedMs={ElapsedMs}", timer.ElapsedMilliseconds);
            throw;
        }
    }

    protected override string GetAssetUrlFromName(GithubRelease release, string assetName)
    {
        try
        {
            return base.GetAssetUrlFromName(release, assetName);
        }
        catch (Exception ex)
        {
            // 基类会跳过缺失更新索引的 release；默认 Trace 日志不足以解释空 feed。
            logger.LogWarning(ex, "GitHub release asset unavailable. Release={Release} Asset={Asset}",
                release.Name, assetName);
            throw;
        }
    }

    public override async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger veloLogger, string? appId,
        string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
    {
        var timer = Stopwatch.StartNew();
        logger.LogInformation("Update feed request started. AppId={AppId} Channel={Channel}", appId, channel);
        try
        {
            var feed = await base.GetReleaseFeed(veloLogger, appId, channel, stagingId, latestLocalRelease)
                .ConfigureAwait(false);
            var full = feed.Assets.Where(a => a.Type == VelopackAssetType.Full).ToArray();
            logger.LogInformation(
                "Update feed received. Channel={Channel} AssetCount={AssetCount} FullCount={FullCount} LatestVersion={LatestVersion} ElapsedMs={ElapsedMs}",
                channel, feed.Assets.Length, full.Length,
                full.OrderByDescending(a => a.Version).FirstOrDefault()?.Version?.ToNormalizedString(),
                timer.ElapsedMilliseconds);
            if (full.Length == 0)
                logger.LogWarning("Update feed has no full package; an empty check result does not confirm the app is up to date. Channel={Channel}", channel);
            return feed;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Update feed request failed. Channel={Channel} ElapsedMs={ElapsedMs}", channel, timer.ElapsedMilliseconds);
            throw;
        }
    }
}
