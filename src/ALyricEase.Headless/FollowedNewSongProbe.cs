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

        // 货架卡片:同一张专辑的多首只占一格。单曲 / 专辑两条货架各自上限 24,
        // 所以 Items 的总数可以是 48(24 张单曲 + 24 张专辑)。
        var albums = result.Items.Count(item => item.IsAlbum);
        var singles = result.Items.Count - albums;
        Console.WriteLine($"[followed-new] 货架 {result.Items.Count} 张卡(专辑卡 {albums} 张 / 单曲卡 " +
                          $"{singles} 张) 队列 {result.Songs.Count} 首");
        foreach (var item in result.Items.Take(5))
            Console.WriteLine("[followed-new]   " +
                              (item.IsAlbum
                                  ? $"专辑《{item.Album}》{item.Songs.Count} 首 — {item.Lead.Artist}"
                                  : $"单曲 {item.Lead.Name} — {item.Lead.Artist}"));

        // 专辑卡上的曲数必须是**专辑真实曲数**（采样数会被用户当场质疑:BELOVED 专辑 10 首、
        // 采样只有 4 首）。服务层已为每张卡补了全量曲目,这里逐张打印并核对。
        var albumCards = result.Items.Where(item => item.IsAlbum).ToList();
        foreach (var item in albumCards)
        {
            Console.WriteLine($"[followed-new]   专辑卡《{item.Album}》采样 {item.Songs.Count} 首 / " +
                              $"全量 {(item.HasFullAlbum ? $"{item.TotalCount} 首" : "未拿到(回落采样)")}" +
                              $" — {item.Lead.Artist}");
        }

        var missingFull = albumCards.Count(item => !item.HasFullAlbum);
        Console.WriteLine(missingFull == 0
            ? "[followed-new] PASS 专辑卡都拿到了整张曲目(曲数=专辑真实曲数)"
            : $"[followed-new] WARN {missingFull} 张专辑没拉到全量曲目,卡片会写「N 首新歌」");

        var shrunk = albumCards.Where(item => item.HasFullAlbum && item.TotalCount < item.Songs.Count)
            .ToList();
        Console.WriteLine(shrunk.Count == 0
            ? "[followed-new] PASS 全量曲目不少于采样"
            : $"[followed-new] FAIL 全量比采样还少:{string.Join("、", shrunk.Select(item => item.Album))}");

        // 再拿一次 QQ 的批量曲数做交叉核对:全量曲目接口若被 limit 截断,这里会暴露。
        var qqMids = albumCards.Where(item => item.Lead.Source == MusicSource.QQ)
            .Select(item => item.Lead.AlbumMid).Where(mid => mid.Length > 0).Distinct().ToList();
        if (qqMids.Count > 0)
        {
            var counts = await qq.GetAlbumSongCountsAsync(qqMids).ConfigureAwait(true);
            var mismatch = albumCards
                .Where(item => item.Lead.Source == MusicSource.QQ)
                .Select(item => (item.Album, Card: item.TotalCount,
                    Real: counts.GetValueOrDefault(item.Lead.AlbumMid)))
                .Where(pair => pair.Real > 0 && pair.Real != pair.Card)
                .ToList();
            Console.WriteLine(mismatch.Count == 0
                ? "[followed-new] PASS 专辑卡曲数与接口 totalNum 一致"
                : "[followed-new] FAIL 曲数不一致:" + string.Join("、",
                    mismatch.Select(pair => $"{pair.Album} 卡片 {pair.Card} / 接口 {pair.Real}")));
        }

        var overLimit = singles > 24 || albums > 24;
        Console.WriteLine(overLimit
            ? $"[followed-new] FAIL 某一类卡片超过上限(24):单曲 {singles} / 专辑 {albums}"
            : "[followed-new] PASS 两类卡片各自在上限内");

        // 同一个专辑键被拆成两张卡 = 聚合漏了(比如 albumId/albumMid 混用)。
        var duplicateAlbum = result.Items
            .Where(item => item.IsAlbum)
            .GroupBy(item => item.Album + "\u0001" + item.Lead.Artist)
            .FirstOrDefault(group => group.Count() > 1);
        Console.WriteLine(duplicateAlbum is null
            ? "[followed-new] PASS 同一专辑只出现一张卡"
            : $"[followed-new] FAIL 专辑《{duplicateAlbum.Key}》被拆成 {duplicateAlbum.Count()} 张卡");

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

        var ok = outside == 0 && descending && !overLimit && duplicateAlbum is null
                 && result.Songs.Count > 0;
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
