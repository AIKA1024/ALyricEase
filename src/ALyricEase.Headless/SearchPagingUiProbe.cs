using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services.NetEase;
using ALyricEase.Services.QQMusic;
using ALyricEase.ViewModels;
using ALyricEase.Views;

namespace ALyricEase.Headless;

/// <summary>
/// 搜索页"滚到底自动续页"的端到端探针（--search-paging-ui）。
///
/// 走真实 SearchView + 真实网络:QQ 单源搜"周杰伦" → 切歌曲 Tab → 把 ResultsScroll 滚到底,
/// 断言 ①滚动触发了一次续页 ②列表条数增加 ③同一首歌没有出现两次。
/// 只读联网;不截图。
/// </summary>
internal static class SearchPagingUiProbe
{
    public static async Task<int> RunAsync()
    {
        var (musicU, qqCookie) = FollowedNewSongProbe.ReadCookies();
        var ne = ServiceLocator.Get<NetEaseApiClient>();
        var qq = ServiceLocator.Get<QQMusicApiClient>();
        if (musicU.Length > 0) ne.SetMusicUCookie(musicU);
        if (qqCookie.Length > 0) qq.SetCookie(qqCookie);

        var vm = ServiceLocator.Get<SearchViewModel>();
        vm.SelectQQSourceCommand.Execute(null);
        var window = new Window
        {
            Width = 900,
            Height = 700,
            Content = new SearchView { DataContext = vm },
        };
        window.Show();
        await PumpAsync();

        // 首次搜索:默认落"全部"页(每区 5 条),再切到歌曲 Tab 拿满页
        vm.Keyword = "周杰伦";
        vm.SearchCommand.Execute(null);
        await WaitAsync(() => !vm.IsSearching, 20000);
        vm.SelectedTabIndex = 1;
        await WaitAsync(() => !vm.IsSearching && vm.Songs.Count > 0, 20000);

        var firstCount = vm.Songs.Count;
        Console.WriteLine($"[search-paging-ui] 首屏 歌曲={firstCount} HasMore={vm.HasMore}");

        var sv = window.GetVisualDescendants().OfType<ScrollViewer>()
            .FirstOrDefault(s => s.Name == "ResultsScroll");
        if (sv is null)
        {
            Console.WriteLine("[search-paging-ui] FAIL 找不到 ResultsScroll");
            return 1;
        }
        Console.WriteLine($"[search-paging-ui] 滚动区 extent={sv.Extent.Height:F0} viewport={sv.Viewport.Height:F0}");

        sv.Offset = new Vector(0, Math.Max(0, sv.Extent.Height - sv.Viewport.Height));
        await PumpAsync();
        var grew = await WaitAsync(() => vm.Songs.Count > firstCount, 20000);
        var after = vm.Songs.Count;
        var distinct = vm.Songs.Select(s => (s.Song.Source, s.Song.Id, s.Song.Mid)).Distinct().Count();
        Console.WriteLine($"[search-paging-ui] 续页后 歌曲={after} 去重={distinct} HasMore={vm.HasMore}");
        Console.WriteLine($"[search-paging-ui] 尾行 {vm.Songs[^1].Name}");

        // 再滚 3 轮:虚拟化生效后不该因为行数增长而卡住/重复
        var rounds = 0;
        while (vm.HasMore && rounds < 3)
        {
            var before = vm.Songs.Count;
            sv.Offset = new Vector(0, Math.Max(0, sv.Extent.Height - sv.Viewport.Height));
            await PumpAsync();
            await WaitAsync(() => vm.Songs.Count > before || !vm.HasMore, 20000);
            rounds++;
            Console.WriteLine($"[search-paging-ui] 第 {rounds} 轮 歌曲={vm.Songs.Count} HasMore={vm.HasMore}");
            if (vm.Songs.Count == before && vm.HasMore)
            {
                Console.WriteLine("[search-paging-ui] 条数不再增长但 HasMore 仍为 true ⇒ 会无限续页");
                break;
            }
        }
        var realized = window.GetVisualDescendants().OfType<TrackRow>().Count();
        var finalDistinct = vm.Songs.Select(s => (s.Song.Source, s.Song.Id, s.Song.Mid)).Distinct().Count();
        Console.WriteLine($"[search-paging-ui] 收尾 歌曲={vm.Songs.Count} 去重={finalDistinct} " +
                          $"HasMore={vm.HasMore} 实化 TrackRow={realized}");

        if (!grew || after <= firstCount || distinct != after || finalDistinct != vm.Songs.Count)
        {
            Console.WriteLine($"[search-paging-ui] FAIL grew={grew} before={firstCount} after={after} " +
                              $"distinct={distinct} finalDistinct={finalDistinct}");
            return 1;
        }
        // 虚拟化:300 条也只实化视口附近的行(换成 StackPanel 会等于数据行数)
        if (realized >= vm.Songs.Count)
        {
            Console.WriteLine($"[search-paging-ui] FAIL 虚拟化没生效:实化 {realized} / 数据 {vm.Songs.Count}");
            return 1;
        }

        // 其余单类型 Tab:切过去后直接调 VM 的续页(列分派与去重是否跟着 kind 走)
        var tabBad = 0;
        var artistEndOk = false;
        foreach (var (index, kind) in new[]
                 {
                     (2, SearchKind.Album), (3, SearchKind.Artist),
                     (4, SearchKind.Playlist), (5, SearchKind.User),
                 })
        {
            vm.SelectedTabIndex = index;
            await WaitAsync(() => !vm.IsSearching, 20000);
            var tabBefore = CountOf(vm, kind);
            var hasMore = vm.HasMore;
            if (hasMore)
            {
                await vm.LoadMoreAsync();
                await PumpAsync();
                await WaitAsync(() => !vm.IsLoadingMore, 20000);
            }
            var tabAfter = CountOf(vm, kind);
            var ok = !hasMore || tabAfter > tabBefore;
            if (!ok) tabBad++;
            if (kind == SearchKind.Artist && !hasMore && vm.ShowNoMore) artistEndOk = true;
            Console.WriteLine($"[search-paging-ui] {kind,-8} 首屏={tabBefore} 续页={tabAfter} " +
                              $"HasMore={hasMore} {(ok ? "OK" : "!!")}");
        }

        if (tabBad > 0)
        {
            Console.WriteLine($"[search-paging-ui] FAIL {tabBad} 个 Tab 续页没增长");
            return 1;
        }
        if (!artistEndOk)
        {
            Console.WriteLine("[search-paging-ui] FAIL 表演者到底后页脚没显示「没有更多结果」");
            return 1;
        }

        // 切走再切回歌曲 Tab:虚拟化列表被 IsVisible 隐藏后重新显示,容器必须重新实化
        // (Avalonia #15194:隐藏/显示虚拟化列表后只剩一行)
        vm.SelectedTabIndex = 2;
        await WaitAsync(() => !vm.IsSearching, 20000);
        vm.SelectedTabIndex = 1;
        await WaitAsync(() => !vm.IsSearching, 20000);
        var backRealized = window.GetVisualDescendants().OfType<TrackRow>().Count();
        Console.WriteLine($"[search-paging-ui] 切回歌曲 Tab: 数据={vm.Songs.Count} 实化={backRealized}");
        if (backRealized < 2)
        {
            Console.WriteLine("[search-paging-ui] FAIL 切回 Tab 后虚拟化列表没重新实化(疑似 #15194)");
            return 1;
        }

        // 综合模式(两源合并):只有歌曲 Tab,续页要能继续推进(合并去重后条数可能不足 limit)
        vm.SelectCombinedSourceCommand.Execute(null);
        await WaitAsync(() => !vm.IsSearching, 20000);
        vm.Keyword = "周杰伦";
        vm.SearchCommand.Execute(null);
        await WaitAsync(() => !vm.IsSearching && vm.Songs.Count > 0, 20000);
        var combinedBefore = vm.Songs.Count;
        if (vm.HasMore)
        {
            await vm.LoadMoreAsync();
            await PumpAsync();
            await WaitAsync(() => !vm.IsLoadingMore, 20000);
        }
        var combinedAfter = vm.Songs.Count;
        Console.WriteLine($"[search-paging-ui] 综合模式 首屏={combinedBefore} 续页={combinedAfter} " +
                          $"HasMore={vm.HasMore}");
        if (combinedBefore > 0 && combinedAfter <= combinedBefore)
        {
            Console.WriteLine("[search-paging-ui] FAIL 综合模式续页没有新增");
            return 1;
        }

        Console.WriteLine("[search-paging-ui] PASS 滚到底自动追加下一页、无重复行、到底收尾正确");
        return 0;
    }

    private static int CountOf(SearchViewModel vm, SearchKind kind) => kind switch
    {
        SearchKind.Track => vm.Songs.Count,
        SearchKind.Album => vm.Albums.Count,
        SearchKind.Artist => vm.Artists.Count,
        SearchKind.Playlist => vm.Playlists.Count,
        SearchKind.User => vm.Users.Count,
        _ => 0,
    };

    private static async Task PumpAsync()
    {
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(30);
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task<bool> WaitAsync(Func<bool> done, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            Dispatcher.UIThread.RunJobs();
            if (done()) return true;
            await Task.Delay(60);
        }
        return done();
    }
}
