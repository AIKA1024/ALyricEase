using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using ALyricEase.Services;
using ALyricEase.Services.Auth;
using ALyricEase.Services.NetEase;

namespace ALyricEase.Headless;

/// <summary>网易云歌单加载计时探针(--nefill):完全复刻歌单页的加载路径 ——
/// v6 概览(完整 trackIds + 前缀曲目) → 按 fillTo=200/1000 分批 song/detail 补页,
/// 每一步打印真实耗时,定位"正在加载更多歌曲…但慢得不像话"发生在哪一环。</summary>
public static class NetEasePlaylistFillProbe
{
    public static async Task<int> RunAsync()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var api = new NetEaseApiClient(new ALyricEase.Services.Crypto.CryptoService(), new CnIpPool(), new CookieStore());
        var total = Stopwatch.StartNew();

        // 1) 登录态下取资料拿 uid → 用户歌单 → "喜欢的音乐"(歌单页"我的收藏"默认入口)
        var profile = await api.GetUserProfileAsync();
        var uid = profile.UserId;
        var playlists = await api.GetUserPlaylistsAsync(uid);
        var likedId = api.LikedPlaylistId != 0
            ? api.LikedPlaylistId
            : playlists.FirstOrDefault(p => p.Name.Contains("喜欢的音乐"))?.Id ?? 0;
        var liked = playlists.FirstOrDefault(p => p.Id == likedId);
        if (likedId == 0)
        {
            Console.WriteLine("[nefill][FAIL] 未定位到喜欢的音乐歌单(登录态失效?)");
            return 1;
        }
        Console.WriteLine($"[nefill] 歌单 [{liked?.Name ?? likedId.ToString()}] id={likedId} uid={uid} (获取资料+歌单列表耗时 {total.ElapsedMilliseconds}ms)");

        // 2) v6 概览:完整 trackIds + 前缀完整曲目(即 UI 首屏的"10 首")
        var sw = Stopwatch.StartNew();
        var overview = await api.GetPlaylistTrackOverviewAsync(liked.Id);
        Console.WriteLine($"[nefill] 概览: trackIds={overview.TrackIds.Count}, 前缀完整曲目={overview.PrefixTracks.Count}, 耗时={sw.ElapsedMilliseconds}ms");

        // 3) 复刻 PlaylistViewModel 的补页:fillTo=200(应用首屏行为),逐批 100
        var known = overview.PrefixTracks.Where(s => s.Id != 0).ToDictionary(s => s.Id);
        var trackIds = overview.TrackIds;
        var materialized = 0;
        while (materialized < trackIds.Count && known.ContainsKey(trackIds[materialized]))
            materialized++;

        async Task FillAsync(int fillTo, string tag)
        {
            var target = Math.Min(fillTo, trackIds.Count);
            var batch = 0;
            var swFill = Stopwatch.StartNew();
            var emptyStreak = 0;
            while (materialized < target)
            {
                batch++;
                var slice = new System.Collections.Generic.List<long>();
                for (var i = materialized; i < trackIds.Count && slice.Count < 100; i++)
                    if (!known.ContainsKey(trackIds[i])) slice.Add(trackIds[i]);
                if (slice.Count == 0) break;
                var swBatch = Stopwatch.StartNew();
                System.Collections.Generic.List<ALyricEase.Models.Song> songs;
                try
                {
                    songs = await api.GetSongsByIdsAsync(slice);
                }
                catch (ApiException ex) when (ex.Code == NetEaseApiClient.ThrottledCode)
                {
                    Console.WriteLine($"[{tag}] 批#{batch}: 限速(405), 按空批退避");
                    songs = [];
                }
                foreach (var s in songs.Where(s => s.Id != 0)) known[s.Id] = s;
                while (materialized < trackIds.Count && known.ContainsKey(trackIds[materialized]))
                    materialized++;
                Console.WriteLine($"[{tag}] 批#{batch}: 请求 {slice.Count} 个 id → 返回 {songs.Count} 首, " +
                                  $"推进到 {materialized}, 批耗时={swBatch.ElapsedMilliseconds}ms");
                // 与 PlaylistViewModel 同款保护:整批无推进(限速 405/失效 id)退避一次,连续两次无推进则停
                if (songs.Count == 0)
                {
                    if (++emptyStreak >= 2)
                    {
                        Console.WriteLine($"[{tag}] 连续 {emptyStreak} 批无返回(限速/失效),停止");
                        break;
                    }
                    await Task.Delay(1500);
                }
                else emptyStreak = 0;
            }
            Console.WriteLine($"[{tag}] 补到 {materialized}/{trackIds.Count} 首, 总耗时={swFill.ElapsedMilliseconds}ms");
        }

        await FillAsync(200, "fill200");
        // 4) 更大的补页量(滚动场景),观察是否有递增变慢(限速/风控)
        await FillAsync(Math.Min(trackIds.Count, 1000), "fill1000");

        Console.WriteLine($"[nefill] 总耗时 {total.ElapsedMilliseconds}ms");
        return 0;
    }
}
