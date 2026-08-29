using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services.NetEase;
using ALyricEase.Services.QQMusic;
using ALyricEase.ViewModels;
using ALyricEase.Views;

namespace ALyricEase.Headless;

/// <summary>性能探针(--probe):验证 修复3(过渡期不画真封面)+ 修复5(精简页面) 的实际效果。
/// 关键指标:切回个性推荐时"首帧(不含封面)"成本应回落到占位图水平,换封面成本移出首帧。</summary>
public static class PerfProbe
{
    private static Window? _win;
    private static MainViewModel? _vm;

    public static void Run()
    {
        _win = new Window
        {
            Width = 1200,
            Height = 720,
            Content = new AppShell { DataContext = ServiceLocator.Get<MainViewModel>() },
        };
        _win.Show();
        Drain();

        _vm = ServiceLocator.Get<MainViewModel>();
        PopulateSyntheticData();
        Drain();

        // ── 真实网络:启动即拉首页 + 封面下载期 UI 停顿 ──
        var realVm = new RecommendViewModel(
            ServiceLocator.Get<NetEaseApiClient>(),
            ServiceLocator.Get<QQMusicApiClient>(),
            ServiceLocator.Get<DispatcherService>(),
            _vm.Player);
        var swReal = Stopwatch.StartNew();
        var load = realVm.EnsureLoadedAsync();
        while (!load.IsCompleted)
        {
            Thread.Sleep(20);
            Drain();
        }
        swReal.Stop();
        Console.WriteLine($"[real] EnsureLoadedAsync(4 区块真实请求)完成: {swReal.Elapsed.TotalMilliseconds:F0} ms," +
            $"区块数={realVm.Sections.Count}");

        var realWin = new Window { Width = 1200, Height = 720, Content = new RecommendView { DataContext = realVm } };
        realWin.Show();
        Drain();
        var swCover = Stopwatch.StartNew();
        var maxDrain = 0.0;
        var drains = 0;
        while (swCover.Elapsed.TotalSeconds < 12)
        {
            var swD = Stopwatch.StartNew();
            Drain();
            swD.Stop();
            maxDrain = Math.Max(maxDrain, swD.Elapsed.TotalMilliseconds);
            drains++;
            Thread.Sleep(16);
        }
        Console.WriteLine($"[real] 封面下载期 12s: 单次 UI 排空最大 {maxDrain:F1} ms / {drains} 次");
        realWin.Close();
        Drain();

        // ── 修复3 验证:封面已缓存时,首帧应只画占位图;换封面成本移出首帧 ──
        // 用全新卡片 VM(真实封面 URL),先把封面全部下载进缓存并设好 Cover
        var preCards = realVm.Sections.SelectMany(s => s.Items.OfType<RecommendCardViewModel>())
            .Select(c => new RecommendCardViewModel(c.Title, c.Subtitle, GetCoverUrl(c), c.PlayCount))
            .ToList();
        foreach (var c in preCards) c.EnsureCoverLoaded();
        var preSw = Stopwatch.StartNew();
        while (preCards.Any(c => c.Cover is null) && preSw.Elapsed.TotalSeconds < 30)
        {
            Thread.Sleep(20);
            Drain();
        }
        Console.WriteLine($"[fix3] 预加载封面: {preSw.Elapsed.TotalMilliseconds:F0} ms,已缓存 {preCards.Count(c => c.Cover is not null)}/{preCards.Count}");

        var preVm = new RecommendViewModel(
            ServiceLocator.Get<NetEaseApiClient>(),
            ServiceLocator.Get<QQMusicApiClient>(),
            ServiceLocator.Get<DispatcherService>(),
            _vm.Player);
        var sections = new List<RecommendSectionViewModel>();
        var cardIdx = 0;
        foreach (var sec in realVm.Sections)
        {
            var items = sec.Items
                .Select(obj => obj is RecommendCardViewModel
                    ? (object)preCards[cardIdx++ % preCards.Count]
                    : obj)
                .ToList();
            sections.Add(new RecommendSectionViewModel(sec.Title, items, sec.IsBordered));
        }
        foreach (var s in sections) preVm.Sections.Add(s);

        var fv = new RecommendView { DataContext = preVm };
        var fw = new Window { Width = 1200, Height = 720, Content = fv };
        fw.Show();
        var swFirst = Stopwatch.StartNew();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Loaded); // 首帧:布局+渲染,不跑 Background 换封面任务
        swFirst.Stop();
        var firstRevealed = preCards.Count(c => c.DisplayCover is not null);
        var cardBounds = fw.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(b => b.Classes.Contains("card"))?.Bounds.Width ?? 0;
        var dailyVisible = fw.GetVisualDescendants().OfType<Border>()
            .Any(b => b.Classes.Contains("daily-container") && b.IsVisible);
        Console.WriteLine($"[fix3] 首帧(封面延后,只画占位图): {swFirst.Elapsed.TotalMilliseconds:F1} ms," +
            $"已亮出封面={firstRevealed}/{preCards.Count}(期望 0),卡片宽={cardBounds:F0}(期望 200),每日容器可见={dailyVisible}");
        var swFlip = Stopwatch.StartNew();
        Drain(); // 全部排空 → Background 换封面任务执行
        swFlip.Stop();
        var afterRevealed = preCards.Count(c => c.DisplayCover is not null);
        Console.WriteLine($"[fix3] 换封面(过渡后): {swFlip.Elapsed.TotalMilliseconds:F1} ms," +
            $"已亮出封面={afterRevealed}/{preCards.Count}(期望全部)");
        fw.Close();
        Drain();

        // 第二次挂树(模拟"切走再切回"):封面已在 VM 上,首帧应仍然只画占位图
        var fv2 = new RecommendView { DataContext = preVm };
        var fw2 = new Window { Width = 1200, Height = 720, Content = fv2 };
        fw2.Show();
        var swFirst2 = Stopwatch.StartNew();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Loaded);
        swFirst2.Stop();
        var first2Revealed = preCards.Count(c => c.DisplayCover is not null);
        Console.WriteLine($"[fix3] 第二次挂树首帧(期望仍延后): {swFirst2.Elapsed.TotalMilliseconds:F1} ms," +
            $"已亮出封面={first2Revealed}/{preCards.Count}(期望 0)");
        Drain();
        var after2Revealed = preCards.Count(c => c.DisplayCover is not null);
        Console.WriteLine($"[fix3] 第二次挂树换封面后: 已亮出={after2Revealed}/{preCards.Count}(期望全部)");
        fw2.Close();
        Drain();

        // ── 修复5 验证:合并后区块布局(每日 340 高 + 5 行,卡片区自动高 + 6 卡) ──
        _vm.ActivePage = "Browse";
        Drain();
        _vm.ActivePage = "Recommend";
        Drain();
        var svHeights = _win.GetVisualDescendants().OfType<ScrollViewer>()
            .Select(s => s.Height).ToList();
        var cardsRealized = _win.GetVisualDescendants()
            .SelectMany(v => v is ContentPresenter cp && cp.DataContext is RecommendCardViewModel ? new[] { cp } : Array.Empty<ContentPresenter>())
            .Count();
        Console.WriteLine($"[fix5] 区块 ScrollViewer 高度集=[{string.Join(",", svHeights.Select(h => h.ToString("F0")))}]" +
            $",已实化卡片容器={cardsRealized}(期望 24),每日行={_win.GetVisualDescendants().OfType<TrackRow>().Count()}(期望 6)");

        Console.WriteLine("== TCC 切页耗时(合成数据,对照)==");
        MeasureSwitch("个性推荐(4区块×6项)", "Recommend");
        MeasureSwitch("搜索(30 结果)", "Search");
        MeasureSwitch("我的收藏(200 曲)", "Favorites");
        MeasureSwitch("浏览(占位页)", "Browse");
    }

    private static string GetCoverUrl(RecommendCardViewModel card)
        => (string)typeof(RecommendCardViewModel).GetField("_coverUrl", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(card)!;

    /// <summary>合成首页数据(不走网络,封面全空 → 共享占位图)。</summary>
    private static void PopulateSyntheticData()
    {
        var vm = _vm!;

        vm.Recommend.Sections.Clear();
        var cards = Enumerable.Range(0, 6)
            .Select(i => (object)new RecommendCardViewModel($"推荐歌单 {i}", "副标题", "", 2_300_000))
            .ToList();
        vm.Recommend.Sections.Add(new RecommendSectionViewModel("推荐歌单", cards));
        vm.Recommend.Sections.Add(new RecommendSectionViewModel("热门歌曲", cards));
        vm.Recommend.Sections.Add(new RecommendSectionViewModel("猜你喜欢", cards));
        var daily = Enumerable.Range(0, 6)
            .Select(i => (object)new SongItemViewModel(
                new Song { Id = i + 1, Name = $"每日歌曲 {i}", Artist = "歌手", Album = "专辑", DurationMs = 200_000 },
                (_, _, _) => Task.FromResult(true), api: null, source: "每日歌曲推荐"))
            .ToList();
        vm.Recommend.Sections.Add(new RecommendSectionViewModel("每日歌曲推荐", daily, isBordered: true));

        // 屏蔽 EnsureLoadedAsync 网络重载(切回 Recommend 时会触发)
        typeof(RecommendViewModel).GetField("_loaded", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.SetValue(vm.Recommend, true);

        // 搜索:30 条结果(结果页歌曲分区)
        vm.Search.HasSearched = true;
        vm.Search.Songs.Clear();
        for (var i = 0; i < 30; i++)
            vm.Search.Songs.Add(new SongItemViewModel(
                new Song { Id = 100 + i, Name = $"搜索结果 {i}", Artist = "歌手", Album = "专辑", DurationMs = 180_000 },
                (_, _, _) => Task.FromResult(true)));

        // 我的收藏:200 首
        vm.Playlist.PlaylistTitle = "我喜欢的音乐";
        vm.Playlist.Tracks.Clear();
        for (var i = 0; i < 200; i++)
            vm.Playlist.Tracks.Add(new SongItemViewModel(
                new Song { Id = 200 + i, Name = $"收藏歌曲 {i}", Artist = "歌手", Album = "专辑", DurationMs = 210_000 },
                (_, _, _) => Task.FromResult(true), index: i + 1));
    }

    private static void MeasureSwitch(string name, string page)
    {
        var best = double.MaxValue;
        for (var i = 0; i < 3; i++)
        {
            _vm!.ActivePage = "Browse";
            Drain();
            var sw = Stopwatch.StartNew();
            _vm.ActivePage = page;
            Drain();
            sw.Stop();
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
        }
        Console.WriteLine($"[perf] 切到 {name}: {best:F1} ms(3 次取最小)");
    }

    /// <summary>清空 UI 线程队列(多轮消化动画)。</summary>
    private static void Drain()
    {
        for (var i = 0; i < 6; i++)
            Dispatcher.UIThread.RunJobs();
    }
}
