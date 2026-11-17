using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.Services.QQMusic;
using ALyricEase.ViewModels;

namespace ALyricEase.Headless;

/// <summary>QQ 歌单页分页回归(--qq-pl-paging &lt;tid&gt;):复刻"点开歌单 → 滚到底"的整条链 ——
/// 首屏是否先上屏、余下几页有没有补齐、续页会不会重复/漏歌。
/// 背景(2026-11 用户报"QQ 歌单滑到底不加载剩下的歌"):当初分页终止判据是
/// "本页条数 == pageSize",而 uniform_get_Dissinfo 会剔掉失效条目 —— 标称 1082 首的歌单
/// 首页只回 295 条就被判成"到底",后 787 首再也拉不到。</summary>
internal static class QqPlaylistPagingProbe
{
    public static async Task<int> RunAsync(long tid)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var api = ServiceLocator.Get<QQMusicApiClient>();
        if (!api.IsLoggedIn)
        {
            Console.WriteLine("[qq-pl][FAIL] 本机无有效 QQ Cookie");
            return 1;
        }

        // 真相基线:与 UI 无关地按 300 逐页拉一遍(song_begin 以请求页长推进)
        var expected = new List<long>();
        var pageCount = 0;
        for (var begin = 0; begin < 20000; begin += 300)
        {
            var page = await api.GetPlaylistTrackPageAsync(tid, begin, 300);
            pageCount++;
            Console.WriteLine($"[qq-pl] 基线 begin={begin}: {page.Songs.Count} 首, total={page.TotalCount}, hasMore={page.HasMore}");
            if (page.Songs.Count == 0) break;
            expected.AddRange(page.Songs.Select(song => song.Id).Where(id => id != 0));
        }
        var expectedUnique = expected.Distinct().Count();
        Console.WriteLine($"[qq-pl] 基线合计 {expected.Count} 行 / {expectedUnique} 首去重, {pageCount} 页");

        var vm = ServiceLocator.Get<PlaylistViewModel>();
        var item = new PlaylistItemViewModel(new Playlist
        {
            Id = tid,
            Source = MusicSource.QQ,
            Name = "QQ 分页探针",
            TrackCount = expectedUnique,
        });

        var watch = Stopwatch.StartNew();
        var open = vm.OpenQqPlaylistCommand.ExecuteAsync(item);
        long firstScreenMs = -1;
        var firstScreenRows = -1;
        var pokedScroll = false;
        while (!open.IsCompleted)
        {
            if (firstScreenMs < 0 && !vm.IsBusy && vm.Tracks.Count > 0)
            {
                firstScreenMs = watch.ElapsedMilliseconds;
                firstScreenRows = vm.Tracks.Count;
                // 模拟"首屏刚出、用户就滚到底":续页与后台补页共用单飞任务,不能重复请求同一页
                pokedScroll = true;
                _ = vm.LoadMoreAsync();
            }
            await Task.Delay(60);
        }
        await open;
        watch.Stop();

        var rows = vm.Tracks;
        var ids = rows.Select(row => row.Song.Id).Where(id => id != 0).ToList();
        var unique = ids.Distinct().Count();
        Console.WriteLine($"[qq-pl] 首屏 {firstScreenRows} 行 @ {firstScreenMs}ms(含续页总耗时 {watch.ElapsedMilliseconds}ms, 滚动触发={pokedScroll})");
        Console.WriteLine($"[qq-pl] 落地: 行={rows.Count} 去重={unique} 队列={vm.RetainedQueueSongCount} trackIds={vm.RetainedTrackIdCount}");

        var ok = true;
        ok &= Check(rows.Count == expectedUnique, $"行数与真实曲目数不一致({rows.Count} vs {expectedUnique})");
        ok &= Check(unique == ids.Count, $"行里有重复曲目({ids.Count - unique} 条)");
        ok &= Check(vm.RetainedQueueSongCount == rows.Count, "播放队列与行数不一致(播放全部只会播到一部分)");
        ok &= Check(vm.RetainedTrackIdCount == 0, "QQ 歌单不应有 trackId 增量状态");
        ok &= Check(firstScreenRows > 0 && firstScreenMs >= 0, "首屏没有先出第一页(仍在等全量)");
        ok &= Check(rows.Count > 0 && expected.Count > 0 && rows[^1].Song.Id == expected[^1],
            "末行不是歌单最后一首(尾页没有补齐)");

        // 顺序逐位一致(页与页之间按 song_begin 单调推进,不能错位/漏页)
        var mismatch = -1;
        for (var i = 0; i < Math.Min(expected.Count, ids.Count); i++)
            if (expected[i] != ids[i])
            {
                mismatch = i;
                break;
            }
        ok &= Check(mismatch < 0, $"第 {mismatch} 行与歌单顺序不一致");

        // 拉完后再滚到底:不应再改行数(不重复跑最后一页)
        await vm.LoadMoreAsync();
        ok &= Check(vm.Tracks.Count == rows.Count, "拉完后滚动又改了行数");

        // 离页/返回:分页没完时快照必须把 song_begin 游标一起带走,否则返回后行数就冻在那里了
        if (expectedUnique > 300)
        {
            var reopen = vm.OpenQqPlaylistCommand.ExecuteAsync(item);
            while (!reopen.IsCompleted && !(vm.Tracks.Count > 0 && vm.IsLoadingMore))
                await Task.Delay(30);
            vm.CancelCurrentLoad();
            await reopen;
            var partial = vm.Tracks.Count;
            var snapshot = vm.CaptureAndReleaseNavigationSnapshot();
            await vm.RestoreNavigationSnapshotAsync(snapshot!);
            var restored = vm.Tracks.Count;
            await vm.LoadMoreAsync();
            Console.WriteLine($"[qq-pl] 离页快照: 离开时={partial} 行, 返回恢复={restored} 行, 滚动后续到={vm.Tracks.Count} 行");
            ok &= Check(snapshot is not null && restored == partial, "离页快照没有按原样恢复行数");
            ok &= Check(partial < expectedUnique && vm.Tracks.Count > restored, "返回后滚动没有继续补页(游标丢了)");
        }

        Console.WriteLine(ok ? "[qq-pl] PASS" : "[qq-pl] FAIL");
        return ok ? 0 : 1;
    }

    private static bool Check(bool condition, string message)
    {
        if (!condition) Console.WriteLine($"[qq-pl][FAIL] {message}");
        return condition;
    }
}
