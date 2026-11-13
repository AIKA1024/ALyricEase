using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.Services.NetEase;
using ALyricEase.Services.QQMusic;

namespace ALyricEase.Headless;

/// <summary>
/// 搜索页分页探针（--search-paging [关键词]）。回答两件事：
///   ① 两源的单类型搜索真的支持 offset/page 翻页（第二页不是把第一页再给一遍）；
///   ② 第二页与第一页有没有重复条目（QQ 用户通道会超发，去重键要能兜住）。
///
/// 只读联网。判据：每类第二页条数 &gt; 0，且与第一页交集为 0。
/// </summary>
internal static class SearchPagingProbe
{
    private const int PageSize = 30;

    public static async Task<int> RunAsync(string keyword)
    {
        var ne = ServiceLocator.Get<NetEaseApiClient>();
        var qq = ServiceLocator.Get<QQMusicApiClient>();
        var (musicU, qqCookie) = FollowedNewSongProbe.ReadCookies();
        if (musicU.Length > 0) ne.SetMusicUCookie(musicU);
        if (qqCookie.Length > 0) qq.SetCookie(qqCookie);
        Console.WriteLine($"[search-paging] 登录 网易云={ne.IsLoggedIn} QQ={qq.IsLoggedIn} 关键词={keyword}");

        var bad = 0;
        foreach (var (label, api) in new (string, IMusicApi)[] { ("网易云", ne), ("QQ", qq) })
        {
            foreach (var kind in new[]
                     {
                         SearchKind.Track, SearchKind.Album, SearchKind.Artist,
                         SearchKind.Playlist, SearchKind.User,
                     })
            {
                bad += await CheckAsync(api, label, kind, keyword);
            }
        }

        Console.WriteLine(bad == 0
            ? "[search-paging] PASS 两源五种单类型都能翻页且两页不重复"
            : $"[search-paging] WARN {bad} 处翻页异常(见上)");

        // QQ 用户通道专项:limit 传 100 时不会被截断,能看到服务端一页到底给多少条,
        // 从而判断"超发"是分页窗口就是这么大,还是 num_per_page 生效但多给了尾巴。
        await ProbeQqUserWindowAsync(qq, keyword);
        return bad == 0 ? 0 : 1;
    }

    private static async Task ProbeQqUserWindowAsync(QQMusicApiClient qq, string keyword)
    {
        // limit=0:不传 num_per_page,看服务端默认一页给多少
        var p0 = await qq.SearchAllAsync(keyword, SearchKind.User, 0, 0, CancellationToken.None);
        // limit=100:超过 num_per_page 上限(直接回 0 条),证明"干脆不 limit"这条路不通
        var pBig = await qq.SearchAllAsync(keyword, SearchKind.User, 100, 0, CancellationToken.None);
        // 真实路径:首屏 limit=30 服务端回 55 条;VM 按请求条数推进 ⇒ 第二页 offset=30(page=2)
        var p1 = await qq.SearchAllAsync(keyword, SearchKind.User, 30, 0, CancellationToken.None);
        var first = p1?.Users.Select(Key).ToList() ?? [];
        var p2 = await qq.SearchAllAsync(keyword, SearchKind.User, 30, 30, CancellationToken.None);
        var second = p2?.Users.Select(Key).ToList() ?? [];
        var dup = first.Intersect(second).Count();

        Console.WriteLine($"[search-paging] QQ User 不传 num={p0?.Users.Count ?? 0} 条 / " +
                          $"num=100 → {pBig?.Users.Count ?? 0} 条(超限即空)");
        Console.WriteLine($"[search-paging] QQ User 首屏(要 30)= {first.Count} 条; " +
                          $"offset=30 的第二页={second.Count} 条,重叠={dup}; " +
                          $"合计去重={first.Concat(second).Distinct().Count()}");
        Console.WriteLine($"[search-paging]   p1[0]={First(first)}");
        Console.WriteLine($"[search-paging]   p2[0]={First(second)}");
    }

    private static string Key(SearchUserItem u) => $"{u.Name}|{u.EncryptUin}|{u.Id}";

    private static async Task<int> CheckAsync(
        IMusicApi api, string label, SearchKind kind, string keyword)
    {
        try
        {
            var page1 = Names(await api.SearchAllAsync(keyword, kind, PageSize, 0, CancellationToken.None));
            var page2 = Names(await api.SearchAllAsync(keyword, kind, PageSize, PageSize, CancellationToken.None));
            var overlap = page1.Intersect(page2, StringComparer.Ordinal).Count();
            // 第一页不满页 ⇒ 本来就没有下一页,第二页空属正常(QQ 表演者只有 11 位);
            // 个别重复是服务端排序抖动(QQ 用户通道),去重键会挡;整页重复才算没翻页
            var ok = page1.Count > 0
                     && (page2.Count == 0
                         ? page1.Count < PageSize
                         : overlap < Math.Min(page1.Count, page2.Count) / 2);
            Console.WriteLine(
                $"[search-paging] {label} {kind,-8} p1={page1.Count,3} p2={page2.Count,3} " +
                $"重叠={overlap,2} {(ok ? "OK" : "!!")}  p1[0]={First(page1)} p2[0]={First(page2)}");
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[search-paging] {label} {kind,-8} 异常 {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static List<string> Names(SearchAllResult? result) => result is null
        ? []
        : result.Songs.Select(s => $"S:{s.Source}:{s.Id}:{s.Name}")
            .Concat(result.Albums.Select(a => $"L:{a.Source}:{a.Id}:{a.Name}"))
            .Concat(result.Artists.Select(a => $"A:{a.Source}:{a.Id}:{a.Name}"))
            .Concat(result.Playlists.Select(p => $"P:{p.Source}:{p.Id}:{p.Name}"))
            .Concat(result.Users.Select(u => $"U:{u.Source}:{u.Id}:{u.Name}"))
            .ToList();

    private static string First(IReadOnlyList<string> names) =>
        names.Count > 0 ? names[0] : "(空)";
}
