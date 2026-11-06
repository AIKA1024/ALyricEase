using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ALyricEase.Infrastructure;
using ALyricEase.Services;
using ALyricEase.Services.NetEase;
using ALyricEase.Services.QQMusic;

namespace ALyricEase.Headless;

/// <summary>
/// 关注歌手新歌的联网只读探针（--followed-new-songs）。
///
/// 验三件事：
///   ① 两源的"关注列表 → 每位歌手按发行时间取最新"链路能通（网易云 sublist 要 total=true；
///      QQ 用 order=2）；
///   ② 3 个月窗口真的在过滤（打印的日期必须都不早于 cutoff）；
///   ③ 汇总耗时（决定浏览页要不要单独异步加载）。
/// </summary>
internal static class FollowedNewSongProbe
{
    public static async Task<int> RunAsync()
    {
        var ne = ServiceLocator.Get<NetEaseApiClient>();
        var qq = ServiceLocator.Get<QQMusicApiClient>();
        var (musicU, qqCookie) = ReadCookies();
        if (musicU.Length > 0) ne.SetMusicUCookie(musicU);
        if (qqCookie.Length > 0) qq.SetCookie(qqCookie);
        Console.WriteLine($"[followed-new] 登录 网易云={ne.IsLoggedIn} QQ={qq.IsLoggedIn}");
        if (!ne.IsLoggedIn && !qq.IsLoggedIn)
        {
            Console.WriteLine("[followed-new] SKIP 两个平台都没登录");
            return 2;
        }

        var service = ServiceLocator.Get<FollowedNewSongService>();
        var started = DateTime.UtcNow;
        var result = await service.GetAsync(force: true, CancellationToken.None);
        var elapsed = DateTime.UtcNow - started;

        var cutoff = DateOnly.FromDateTime(DateTime.Today).AddMonths(-FollowedNewSongService.LookbackMonths);
        Console.WriteLine($"[followed-new] 歌手 网易云={result.NetEaseArtists} QQ={result.QqArtists} " +
                          $"失败(网易云={result.NetEaseFailed} QQ={result.QqFailed})");
        var neCount = result.Songs.Count(song => song.Source == MusicSource.NetEase);
        var qqCount = result.Songs.Count(song => song.Source == MusicSource.QQ);
        Console.WriteLine($"[followed-new] 合并 {result.Songs.Count} 首(网易云 {neCount} / QQ {qqCount}) " +
                          $"耗时 {elapsed.TotalSeconds:F1}s(cutoff {cutoff:yyyy-MM-dd})");

        var outside = result.Songs.Count(song =>
            !DateOnly.TryParseExact(song.PublishDate, "yyyy-MM-dd", out var date) || date < cutoff);
        Console.WriteLine(outside == 0
            ? "[followed-new] PASS 全部落在 3 个月窗口内"
            : $"[followed-new] FAIL 有 {outside} 首超出窗口");

        foreach (var song in result.Songs.Take(8))
            Console.WriteLine($"[followed-new]   {song.PublishDate} {song.Source,-7} {song.Name} — {song.Artist}");

        var ordered = result.Songs.Select(song => song.PublishDate).ToList();
        var descending = ordered.SequenceEqual(ordered.OrderByDescending(text => text, StringComparer.Ordinal));
        Console.WriteLine(descending
            ? "[followed-new] PASS 按发行日期倒序"
            : "[followed-new] FAIL 顺序不是倒序");

        var ok = outside == 0 && descending && result.Songs.Count > 0;
        Console.WriteLine(ok ? "[followed-new] PASS" : "[followed-new] FAIL 没拿到可用的新歌");
        return ok ? 0 : 1;
    }

    internal static (string MusicU, string QqCookie) ReadCookies()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ALyricEase.Data", "config", "cookie.json");
        if (!File.Exists(path)) return ("", "");
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var musicU = root.TryGetProperty("MusicU", out var mu) ? mu.GetString() ?? "" : "";
            var qq = root.TryGetProperty("QQCookieRaw", out var raw) ? raw.GetString() ?? "" : "";
            return (musicU, qq);
        }
        catch (JsonException)
        {
            return ("", "");
        }
    }
}
