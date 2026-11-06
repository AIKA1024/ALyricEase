using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ALyricEase.Infrastructure;
using ALyricEase.Services.NetEase;
using ALyricEase.Services.QQMusic;
using ALyricEase.ViewModels;
using ALyricEase.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ALyricEase.Headless;

/// <summary>
/// 浏览页「关注新歌」区块的渲染回归（--browse-followed）。
///
/// 数据链路已由 <c>--followed-new-songs</c> 覆盖，这里只验**界面**：
/// 区块出现在货架里、标题右侧有「最近 3 个月 · N 首」、每张卡片带时间徽章、
/// 窄屏不横向溢出。真实登录态 + 真实数据，截图落在临时目录。
/// </summary>
internal static class BrowseFollowedProbe
{
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(90);

    public static async Task<int> RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "aly-browse-followed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var ne = ServiceLocator.Get<NetEaseApiClient>();
        var qq = ServiceLocator.Get<QQMusicApiClient>();
        var (musicU, qqCookie) = FollowedNewSongProbe.ReadCookies();
        if (musicU.Length > 0) ne.SetMusicUCookie(musicU);
        if (qqCookie.Length > 0) qq.SetCookie(qqCookie);
        Console.WriteLine($"[browse-followed] 登录 网易云={ne.IsLoggedIn} QQ={qq.IsLoggedIn}");

        var vm = ServiceLocator.Get<BrowseViewModel>();
        _ = vm.EnsureLoadedAsync();
        await WaitUntilAsync(() => !vm.IsLoading, LoadTimeout, "浏览页首屏超时").ConfigureAwait(true);
        // 关注新歌是独立任务:等它出结果(有卡片 / 明确"没发新歌" / 失败)
        await WaitUntilAsync(
            () => vm.FollowedSection.HasItems
                  || vm.FollowedSection.Caption.Contains("没发新歌", StringComparison.Ordinal)
                  || vm.FollowedSection.Caption.Contains("失败", StringComparison.Ordinal),
            LoadTimeout, "关注新歌超时").ConfigureAwait(true);

        var section = vm.FollowedSection;
        Console.WriteLine($"[browse-followed] 区块 标题「{section.Title}」 说明「{section.Caption}」 " +
                          $"卡片 {section.Items.Count} 张 可见={section.IsSectionVisible}");
        var ok = section.HasItems;
        Console.WriteLine(ok ? "[browse-followed] PASS 关注新歌有内容" : "[browse-followed] FAIL 关注新歌没有内容");

        // 徽章:新歌卡片右上角必须是相对时间,不能空着
        var missingBadge = section.Items.Count(card => !card.HasBadge);
        Console.WriteLine(missingBadge == 0
            ? "[browse-followed] PASS 每张卡片都有时间徽章"
            : $"[browse-followed] FAIL {missingBadge} 张卡片没有徽章");
        if (section.Items.Count > 0)
        {
            var sample = string.Join(" | ", section.Items.Take(3)
                .Select(card => $"{card.Title}({card.Badge})"));
            Console.WriteLine("[browse-followed] 前 3 张: " + sample);
        }

        var view = new BrowseView { DataContext = vm };
        var window = new Window { Width = 1120, Height = 900, Content = view };
        window.Bind(Window.BackgroundProperty, new DynamicResourceExtension("AppBackground"));
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        window.Show();
        await SettleAsync(1600).ConfigureAwait(true);

        // 「关注新歌」在页面第三块(新歌速递之前),按它的位置滚动,一张图里能同时看到它和下面的新歌速递。
        var scroller = view.GetVisualDescendants().OfType<ScrollViewer>()
            .First(scroll => scroll.Classes.Contains("page-scroll"));
        ScrollSectionToTop(view, scroller, section);
        await SettleAsync(700).ConfigureAwait(true);

        CheckNoOverflow(view, "1120px");
        Capture(window, root, "browse-followed-wide");
        Console.WriteLine($"[browse-followed] scroller extent={scroller.Extent.Height:0} " +
                          $"viewport={scroller.Viewport.Height:0} offset={scroller.Offset.Y:0}");

        // 窄屏:卡片是固定宽度横滑,不应把页面撑出横向滚动
        window.Width = 620;
        await SettleAsync(700).ConfigureAwait(true);
        ScrollSectionToTop(view, scroller, section);
        await SettleAsync(400).ConfigureAwait(true);
        CheckNoOverflow(view, "620px");
        Capture(window, root, "browse-followed-narrow");

        window.Close();
        Console.WriteLine("[browse-followed] capture " + root);
        return ok && missingBadge == 0 ? 0 : 1;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string message)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(120).ConfigureAwait(true);
        }
        throw new TimeoutException(message);
    }

    private static async Task SettleAsync(int milliseconds)
    {
        for (var i = 0; i < milliseconds / 40; i++)
        {
            await Task.Delay(40).ConfigureAwait(true);
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>把某个区块滚到吸顶头部下方:直接算 <c>TranslatePoint</c>,不做 BringIntoView
    /// (后者只保证"可见",对齐方式随滚动位置变,截图会一次一个样)。</summary>
    private static void ScrollSectionToTop(Control view, ScrollViewer scroller, BrowseSectionViewModel section)
    {
        var panel = view.GetVisualDescendants().OfType<StackPanel>()
            .First(item => ReferenceEquals(item.DataContext, section));
        var offset = panel.TranslatePoint(new Point(0, 0), scroller);
        if (offset is null) return;
        var target = scroller.Offset.Y + offset.Value.Y - 110; // 110 ≈ 吸顶头部高度,留一点呼吸
        scroller.Offset = new Vector(0, Math.Clamp(target, 0, Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height)));
    }

    private static void CheckNoOverflow(Control view, string label)
    {
        var scroller = view.GetVisualDescendants().OfType<ScrollViewer>()
            .First(scroll => scroll.Classes.Contains("page-scroll"));
        var overflow = scroller.Extent.Width - scroller.Viewport.Width;
        Console.WriteLine($"[browse-followed] {label} 横向 extent={scroller.Extent.Width:0} " +
                          $"viewport={scroller.Viewport.Width:0} 溢出={overflow:0}");
        if (overflow > 1) throw new InvalidOperationException($"{label} 出现横向溢出 {overflow:0}px");
    }

    private static void Capture(Window window, string root, string name)
    {
        var bitmap = new RenderTargetBitmap(new PixelSize((int)window.ClientSize.Width,
            (int)window.ClientSize.Height), new Vector(96, 96));
        bitmap.Render(window);
        var path = Path.Combine(root, name + ".png");
        bitmap.Save(path);
        Console.WriteLine($"[browse-followed] 截图 {name} -> {path}");
    }
}
