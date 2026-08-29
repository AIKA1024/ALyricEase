using System;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.ViewModels;
using ALyricEase.Views;

namespace ALyricEase.Headless;

/// <summary>TEMP-DIAG:搜索页改版(对齐原版 UWP SearchView/SearchResultView)渲染探针。
/// 落地页两态 + 结果页(真实网络搜索"AIKA":全部页 + 歌曲页),输出临时目录 PNG。</summary>
public static class SearchPageProbe
{
    public static void Run()
    {
        // 历史种子直接注入集合(不落盘;HasHistory 经 CollectionChanged 联动)
        var vm = ServiceLocator.Get<SearchViewModel>();
        foreach (var w in new[] { "同花顺", "周杰伦" })
            vm.SearchHistory.Add(w);

        var window = new Window
        {
            Width = 517,
            Height = 800,
            // headless 无系统主题,固定 Dark 对齐目标稿截图
            RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark,
            Content = new SearchView { DataContext = vm },
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();

        Shot(window, "search-landing");
        Console.WriteLine($"[sp] landing: HasHistory={vm.HasHistory} " +
            $"trending={vm.TrendingKeywords.Count} history={vm.SearchHistory.Count}");

        // 宽屏:热门左列 / 历史右列(原版 Wide VisualState)
        window.Width = 1200;
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
        Shot(window, "search-landing-wide");

        // 聚焦搜索框:验证压平的聚焦边框 + teal 下划线
        window.Width = 517;
        Dispatcher.UIThread.RunJobs();
        var box = window.GetVisualDescendants().OfType<TextBox>()
            .First(t => t.Classes.Contains("search-box"));
        box.Focus();
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
        Shot(window, "search-landing-focus");

        // 结果页:真实搜索(结果页需网络;失败时仍渲染空态)
        Console.WriteLine("[sp] 开始真实搜索 AIKA...");
        vm.Keyword = "AIKA";
        vm.SearchCommand.Execute(null);
        WaitIdle(vm, seconds: 30);
        Shot(window, "search-results-all");
        Console.WriteLine($"[sp] results/all: songs={vm.Songs.Count} playlists={vm.Playlists.Count} " +
            $"artists={vm.Artists.Count} albums={vm.Albums.Count} users={vm.Users.Count} " +
            $"message={vm.Message ?? "-"}");

        // 歌曲页(30 条)
        if (vm.Songs.Count > 0)
        {
            vm.SelectedTab = SearchKind.Track;
            WaitIdle(vm, seconds: 30);
            Shot(window, "search-results-track");
            Console.WriteLine($"[sp] results/track: songs={vm.Songs.Count} message={vm.Message ?? "-"}");
        }

        // All 页滚动到底:验证表演者/专辑/用户分区 + "没有更多结果"页脚 + 回顶按钮浮现
        vm.SelectedTab = SearchKind.All;
        WaitIdle(vm, seconds: 30);
        var scroll = window.GetVisualDescendants().OfType<ScrollViewer>()
            .FirstOrDefault(s => s.Name == "ResultsScroll");
        if (scroll is not null)
        {
            scroll.Offset = new Vector(0, scroll.Extent.Height);
            Dispatcher.UIThread.RunJobs();
            Dispatcher.UIThread.RunJobs();
            Console.WriteLine($"[sp] 滚动到底: ShowBackToTop={vm.ShowBackToTop} (期望 True)");
            Shot(window, "search-results-all-bottom");
        }

        // Tab 溢出:517 宽应出现"···";1100 宽应居中无溢出;选中溢出项应被带回可视区
        Button? MoreButton() => window.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(b => b.Name == "TabOverflowButton");
        int VisibleTabs() => window.GetVisualDescendants().OfType<ListBoxItem>()
            .Count(li => li.IsVisible && li.GetVisualAncestors().OfType<ListBox>()
                .Any(lb => lb.Name == "TabItems"));

        Console.WriteLine($"[sp] tabs@517: 可见={VisibleTabs()}/6 ···可见={MoreButton()?.IsVisible} (期望 4/True)");
        Shot(window, "search-tabs-narrow");

        window.Width = 1100;
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
        Console.WriteLine($"[sp] tabs@1100: 可见={VisibleTabs()}/6 ···可见={MoreButton()?.IsVisible} (期望 6/False)");
        Shot(window, "search-tabs-wide");

        window.Width = 517;
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
        vm.SelectedTab = SearchKind.User; // 等价于点开 ··· Flyout 里的"用户"
        WaitIdle(vm, seconds: 30);
        Console.WriteLine($"[sp] tabs@517 选中用户: 可见={VisibleTabs()}/6 ···可见={MoreButton()?.IsVisible} " +
            $"users={vm.Users.Count} (期望 选中项被带回可视区、内容=用户)");
        Shot(window, "search-tabs-user-selected");
        vm.SelectedTab = SearchKind.All;
        WaitIdle(vm, seconds: 30);

        // QQ 音源端到端:回登录页 → 切 QQ → 搜"周杰伦"(歌曲走 client_search_cp,歌单走 musicu.fcg)
        vm.BackToLandingCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        vm.SelectQQSourceCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Console.WriteLine($"[sp] QQ Tab 数={vm.Tabs.Count} ({string.Join("/", vm.Tabs.Select(t => t.Label))})");
        vm.Keyword = "周杰伦";
        vm.SearchCommand.Execute(null);
        WaitIdle(vm, seconds: 30);
        Shot(window, "search-results-qq");
        Console.WriteLine($"[sp] results/qq: songs={vm.Songs.Count} playlists={vm.Playlists.Count} " +
            $"message={vm.Message ?? "-"}");

        Console.WriteLine("[sp] done");
    }

    /// <summary>等待异步搜索完成(最多 seconds 秒;headless 下 RunJobs 推进 Dispatcher)。</summary>
    private static void WaitIdle(SearchViewModel vm, int seconds)
    {
        for (var i = 0; i < seconds * 10 && vm.IsSearching; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(100);
            Dispatcher.UIThread.RunJobs();
        }
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Shot(Window window, string name)
    {
        var w = (int)window.ClientSize.Width;
        var h = (int)window.ClientSize.Height;
        using var rtb = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96));
        rtb.Render(window);
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), name + ".png");
        // Save(string) 重载在 12.1 已过时且为静默空操作(0 字节文件),必须走流
        using (var fs = System.IO.File.Create(path))
            rtb.Save(fs);
        Console.WriteLine($"[sp] 截图 {name}: {path}");
    }
}
