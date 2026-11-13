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
using ALyricEase.ViewModels;
using Avalonia.Threading;

namespace ALyricEase.Headless;

/// <summary>
/// 私人FM 双源逐首交替的联网探针(--fm-mix)。
///
/// 需求原话:"接上吧,不需要切换按钮,放1首就换一个源"。所以判据是:
///   ① QQ 源真的能被拉进 FM(不再是只有网易云);
///   ② 队列里**相邻两首的 Source 不同**(网易云→QQ→网易云…);
///   ③ 连按两次"下一首"后仍然交替(预补/切歌路径都保持轮次)。
/// </summary>
internal static class FmMixProbe
{
    public static async Task<int> RunAsync()
    {
        var ne = ServiceLocator.Get<NetEaseApiClient>();
        var qq = ServiceLocator.Get<QQMusicApiClient>();
        var (musicU, qqCookie) = FollowedNewSongProbe.ReadCookies();
        if (musicU.Length > 0) ne.SetMusicUCookie(musicU);
        if (qqCookie.Length > 0) qq.SetCookie(qqCookie);
        Console.WriteLine($"[fm-mix] 登录 网易云={ne.IsLoggedIn} QQ={qq.IsLoggedIn}");
        if (!ne.IsLoggedIn && !qq.IsLoggedIn)
        {
            Console.WriteLine("[fm-mix] SKIP 两个平台都没登录");
            return 2;
        }

        var player = ServiceLocator.Get<PlayerViewModel>();

        // 诊断:播放器内部拿到的音源表与 QQ 客户端是否就是探针刚注入 Cookie 的那一个
        var provider = ServiceLocator.Get<MusicApiProvider>();
        var qqInProvider = provider.All.OfType<QQMusicApiClient>().FirstOrDefault();
        Console.WriteLine($"[fm-mix] 音源表=[{string.Join(",", provider.All.Select(a => a.Source))}] " +
                          $"QQ实例同一={ReferenceEquals(qq, qqInProvider)}");
        try
        {
            var direct = await qq.GetPersonalFmAsync();
            Console.WriteLine($"[fm-mix] QQ 直取一批={direct.Count} 首");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[fm-mix] QQ 直取异常 {ex.GetType().Name}: {ex.Message}");
        }

        await player.StartPersonalFmAsync();
        await WaitIdleAsync(player);

        var chain = Snapshot(player);
        Console.WriteLine($"[fm-mix] 启动后: IsFmActive={player.IsFmActive} 队列={chain.Count}");
        PrintChain(chain);

        if (!player.IsFmActive || chain.Count == 0)
        {
            Console.WriteLine("[fm-mix] FAIL 私人FM 没起来");
            return 1;
        }

        var sources = chain.Select(s => s.Source).ToList();
        var hasBoth = sources.Contains(MusicSource.NetEase) && sources.Contains(MusicSource.QQ);
        var alternates = IsAlternating(sources);
        Console.WriteLine($"[fm-mix] 两源都有={hasBoth} 相邻不同源={alternates}");

        // 连按 6 次下一首:必须走 FM 的补货路径。这里记录**每次实际切到的当前曲**,
        // 相邻两次的 Source 必须不同 —— 这才是"放 1 首就换一个源"的真正判据。
        var played = new List<MusicSource> { player.CurrentSong!.Source };
        for (var i = 0; i < 6; i++)
        {
            var before = player.CurrentSong;
            await player.PlayNextAsync();
            await WaitIdleAsync(player);
            if (player.CurrentSong is { } now && !ReferenceEquals(now, before))
                played.Add(now.Source);
            Console.WriteLine($"[fm-mix]   第 {i + 1} 次下一首 → [{player.CurrentSong?.Source}] {player.CurrentSong?.Name}");
        }
        var afterNext = Snapshot(player);
        Console.WriteLine($"[fm-mix] 连按 6 次下一首后 队列尾部={afterNext.Count}");
        PrintChain(afterNext);
        var afterAlt = IsAlternating(afterNext.Select(s => s.Source).ToList());
        var playedAlt = IsAlternating(played);
        Console.WriteLine($"[fm-mix] 实际播放换源={playedAlt} 尾部交替={afterAlt} " +
                          $"播放器消息={(player.Message ?? "(无)")}");

        // QQ 曲目必须真的能取到播放地址,否则"交替"只是把不可播的行塞进队列
        var qqPlayable = 0;
        foreach (var song in afterNext.Where(s => s.Source == MusicSource.QQ).Take(2))
        {
            try
            {
                var item = await qq.GetPlayUrlAsync(song, "standard");
                var ok = !string.IsNullOrEmpty(item?.Url);
                if (ok) qqPlayable++;
                Console.WriteLine($"[fm-mix] QQ 取址《{song.Name}》=" +
                                  (ok ? $"OK {(item!.Url!.Length > 60 ? item.Url[..60] + "…" : item.Url)}" : "空(不可播)"));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[fm-mix] QQ 取址《{song.Name}》异常 {ex.GetType().Name}: {ex.Message}");
            }
        }

        if (!hasBoth || !alternates || !afterAlt || !playedAlt || qqPlayable == 0)
        {
            Console.WriteLine($"[fm-mix] FAIL hasBoth={hasBoth} alternates={alternates} " +
                              $"afterNext={afterAlt} playedAlt={playedAlt} QQ可播={qqPlayable}");
            return 1;
        }
        Console.WriteLine("[fm-mix] PASS QQ 已接入私人FM,相邻两首逐首换源");
        return 0;
    }

    /// <summary>当前曲 + "接下来播放"拼成一条链(与用户听到的顺序一致)。</summary>
    private static List<Song> Snapshot(PlayerViewModel player)
    {
        var songs = new List<Song>();
        if (player.CurrentSong is { } current) songs.Add(current);
        songs.AddRange(player.DumpFmUpcomingForProbe());
        return songs;
    }

    /// <summary>相邻两首必须不同源;同源的连续段只允许出现在某一源暂时断货时,
    /// 因此只在两源都出现过的链上判"整体是否严格交替"。</summary>
    private static bool IsAlternating(IReadOnlyList<MusicSource> sources)
    {
        if (sources.Count < 2) return false;
        for (var i = 1; i < sources.Count; i++)
            if (sources[i] == sources[i - 1]) return false;
        return true;
    }

    private static void PrintChain(IReadOnlyList<Song> songs)
    {
        for (var i = 0; i < songs.Count && i < 8; i++)
            Console.WriteLine($"[fm-mix]   {i + 1}. [{songs[i].Source}] {songs[i].Name} — {songs[i].Artist}");
    }

    /// <summary>起播/切歌是异步的:等 IsLoading 落回且当前曲不为空(最多 30s)。</summary>
    private static async Task WaitIdleAsync(PlayerViewModel player)
    {
        for (var i = 0; i < 300; i++)
        {
            Dispatcher.UIThread.RunJobs();
            if (!player.IsLoading && player.CurrentSong is not null) return;
            await Task.Delay(100);
        }
    }
}
