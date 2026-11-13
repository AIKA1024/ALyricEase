using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Models.Dtos;
using ALyricEase.Services;
using ALyricEase.Services.QQMusic;

namespace ALyricEase.Headless;

/// <summary>
/// QQ 歌手专辑列表的 <c>order</c> 语义 —— 联网只读探针（--qq-albums [关键词]）。
///
/// 起因:用户反馈"鸣潮先约电台"在 QQ 源的专辑页里第一屏只有《待春归》,
/// 官方新发的《放课后时光》要滚到下面(第二页之后)才出现。
/// 若 <c>GetAlbumList</c> 的 order=1 不是按发行时间倒序,首页必然不是最新专辑 ——
/// 这与 <c>GetSingerSongList</c>(order=1 热度 / order=2 时间)是同一个坑。
///
/// 做法:同一个 singerMid 用 order=0/1/2/3 各拉第一页,用每张专辑的 publishDate
/// 判定顺序是否降序,并打印目标专辑落在第几条。
/// </summary>
internal static class QqArtistAlbumProbe
{
    public static async Task<int> RunAsync(string keyword)
    {
        var api = ServiceLocator.Get<QQMusicApiClient>();
        var cookie = ReadQqCookie();
        if (!string.IsNullOrEmpty(cookie)) api.SetCookie(cookie);
        Console.WriteLine($"[qq-albums] 登录={api.IsLoggedIn} 关键词={keyword}");

        var search = await api.SearchAllAsync(keyword, SearchKind.Artist, 5, 0, CancellationToken.None);
        var artists = search?.Artists ?? [];
        foreach (var a in artists.Take(5))
            Console.WriteLine($"[qq-albums] 候选 歌手={a.Name} mid={a.Mid} src={a.Source}");
        if (artists.Count == 0)
        {
            Console.WriteLine("[qq-albums] FAIL 搜不到歌手");
            return 1;
        }

        var singer = artists.FirstOrDefault(a => a.Source == MusicSource.QQ && a.Mid.Length > 0)
                     ?? artists[0];
        Console.WriteLine($"[qq-albums] 使用 歌手={singer.Name} mid={singer.Mid}");

        foreach (var order in new[] { 0, 1, 2, 3 })
        {
            try
            {
                var page = await api.GetArtistAlbumPageAsync(
                    singer.Mid, 0, 30, CancellationToken.None, order);
                PrintPage($"order={order}", page, 12);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[qq-albums] order={order} 异常 {ex.GetType().Name}: {ex.Message}");
            }
        }

        // 分页连续性:order=0 连拉 4 页,看有没有跳空/重复(第一页 30 条要覆盖日期区间)
        foreach (var order in new[] { 0, 1 })
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var total = 0;
            for (var begin = 0; begin < 120; begin += 30)
            {
                var page = await api.GetArtistAlbumPageAsync(
                    singer.Mid, begin, 30, CancellationToken.None, order);
                var dup = page.Count(a => !seen.Add(a.Mid));
                total += page.Count;
                Console.WriteLine(
                    $"[qq-albums] order={order} begin={begin} n={page.Count} 重复={dup} " +
                    $"首={Date(page.FirstOrDefault()?.PublishTime ?? 0)} {page.FirstOrDefault()?.Name} " +
                    $"末={Date(page.LastOrDefault()?.PublishTime ?? 0)} {page.LastOrDefault()?.Name}");
            }
            Console.WriteLine($"[qq-albums] order={order} 四页合计 {total} 去重后 {seen.Count}");
        }

        // 单页上限:number 传 50 实际回多少(决定歌手页"取 50 张"能不能凑满)
        foreach (var size in new[] { 30, 50 })
        {
            var page = await api.GetArtistAlbumPageAsync(
                singer.Mid, 0, size, CancellationToken.None, 0);
            Console.WriteLine($"[qq-albums] number={size} 实回={page.Count}");
        }

        // UI 端到端:歌手页横向区 + "全部专辑"页首屏是不是最新在前
        try
        {
            var artistVm = ServiceLocator.Get<ALyricEase.ViewModels.ArtistViewModel>();
            await artistVm.LoadQqAsync(singer.Mid);
            Console.WriteLine($"[qq-albums] 歌手页 专辑={artistVm.Albums.Count} 单曲EP={artistVm.Singles.Count}");
            for (var i = 0; i < Math.Min(5, artistVm.Albums.Count); i++)
                Console.WriteLine($"[qq-albums]   歌手页 {i}. {artistVm.Albums[i].Title}");

            var albumsPage = ServiceLocator.Get<ALyricEase.ViewModels.ArtistAlbumsPageViewModel>();
            await albumsPage.LoadAsync(new ALyricEase.ViewModels.ArtistPageRef(
                MusicSource.QQ, 0, singer.Mid, singer.Name));
            Console.WriteLine($"[qq-albums] 全部专辑页 共={albumsPage.Albums.Count}");
            for (var i = 0; i < Math.Min(5, albumsPage.Albums.Count); i++)
                Console.WriteLine($"[qq-albums]   专辑页 {i}. {albumsPage.Albums[i].Title}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[qq-albums] UI 验证异常 {ex.GetType().Name}: {ex.Message}");
        }

        return 0;
    }

    private static void PrintPage(string label, IReadOnlyList<ArtistAlbumItem> page, int head)
    {
        Console.WriteLine($"[qq-albums] {label} n={page.Count} 降序={IsDescending(page)}");
        for (var i = 0; i < Math.Min(head, page.Count); i++)
        {
            var a = page[i];
            Console.WriteLine($"[qq-albums]   {i,2}. {Date(a.PublishTime)} [{a.Type}] {a.Name}");
        }

        foreach (var name in new[] { "放课后时光", "待春归" })
        {
            var hit = page.ToList().FindIndex(a => a.Name.Contains(name, StringComparison.Ordinal));
            if (hit >= 0) Console.WriteLine($"[qq-albums]   · 命中「{name}」第 {hit} 条");
        }
    }

    /// <summary>按 PublishTime(毫秒)判是否严格降序;0 视为未知跳过。</summary>
    private static bool IsDescending(IReadOnlyList<ArtistAlbumItem> page)
    {
        long prev = long.MaxValue;
        var seen = 0;
        foreach (var a in page)
        {
            if (a.PublishTime <= 0) continue;
            if (a.PublishTime > prev) return false;
            prev = a.PublishTime;
            seen++;
        }
        return seen > 1;
    }

    private static string Date(long ms) => ms <= 0
        ? "----------"
        : DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("yyyy-MM-dd");

    /// <summary>从应用配置里取 QQ Cookie 原文(与 <c>CookieStore</c> 同一个文件)。</summary>
    private static string ReadQqCookie()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ALyricEase.Data", "config", "cookie.json");
        if (!File.Exists(path)) return "";
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("QQCookieRaw", out var value)
                ? value.GetString() ?? ""
                : "";
        }
        catch (JsonException)
        {
            return "";
        }
    }
}
