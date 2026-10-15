using System.Net;
using System.Text;
using ALyricEase.Infrastructure.Logging;
using ALyricEase.Services;
using Microsoft.Extensions.Logging;
using Velopack.Locators;
using Velopack.Logging;
using Velopack.Sources;

namespace ALyricEase;

/// <summary>离线验证真实 GithubSource 的空列表、缺失索引、有效 feed 和请求失败诊断。</summary>
internal static class UpdateLoggingProbe
{
    public static async Task RunAsync()
    {
        var logger = AppLogging.Factory.CreateLogger("UpdateLoggingProbe");
        var veloLogger = VelopackLocator.Current.Log;
        veloLogger.Log(VelopackLogLevel.Information, "logging-probe-velopack-bridge", null);
        veloLogger.Log(VelopackLogLevel.Debug, "logging-probe-debug-velopack-must-not-appear", null);

        const string release = """
            [{"name":"probe-release","assets":[{"name":"releases.win.json","browser_download_url":"https://example.invalid/feed"}]}]
            """;
        var downloader = new FakeDownloader();
        var source = new LoggingGithubSource("https://github.com/example/probe", logger, downloader);
        var empty = await source.GetReleaseFeed(veloLogger, "probe", "win");
        Check(empty.Assets.Length == 0, "empty release list");
        downloader.Releases = """[{"name":"probe-missing-index","assets":[]}]""";
        var missing = await source.GetReleaseFeed(veloLogger, "probe", "win");
        Check(missing.Assets.Length == 0, "missing release index retains library behavior");
        downloader.Releases = release;
        var valid = await source.GetReleaseFeed(veloLogger, "probe", "win");
        Check(valid.Assets.Length == 1 && valid.Assets[0].Version.ToNormalizedString() == "9.0.0", "valid feed");

        foreach (var failFeed in new[] { false, true })
        {
            downloader.FailFeed = failFeed;
            downloader.Error = new HttpRequestException("probe-http-failure https://example.invalid/?token=secret-update",
                null, HttpStatusCode.BadGateway);
            try
            {
                await source.GetReleaseFeed(veloLogger, "probe", "win");
                throw new InvalidOperationException("Update logging probe swallowed request failure");
            }
            catch (HttpRequestException ex) when (ReferenceEquals(ex, downloader.Error)) { }
        }
        logger.LogInformation("logging-probe-update-complete");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Update logging probe failed: " + message);
    }

    private sealed class FakeDownloader : IFileDownloader
    {
        public string Releases { get; set; } = "[]";
        public HttpRequestException? Error { get; set; }
        public bool FailFeed { get; set; }

        public Task<string> DownloadString(string url, IDictionary<string, string>? headers = null, double timeout = 30) =>
            Error is not null && !FailFeed ? Task.FromException<string>(Error) : Task.FromResult(Releases);

        public Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers = null, double timeout = 30) =>
            Error is not null && FailFeed ? Task.FromException<byte[]>(Error) : Task.FromResult(Encoding.UTF8.GetBytes("""
                {"Assets":[{"PackageId":"probe","Version":"9.0.0","Type":"Full","FileName":"probe-full.nupkg","Size":123}]}
                """));

        public Task DownloadFile(string url, string targetFile, Action<int> progress,
            IDictionary<string, string>? headers = null, double timeout = 30, CancellationToken cancelToken = default) =>
            throw new NotSupportedException("The logging probe must not download or install updates");
    }
}
