using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.ViewModels;
using ALyricEase.Views;

namespace ALyricEase.Headless;

/// <summary>侧栏收起态(图标栏)内容回归(--compact-nav):图标栏只允许出现静态主导航
/// (搜索/个性推荐/浏览/私人FM/我的收藏/音乐云盘/最近播放)这 7 行。
///
/// 为什么值得单开一条回归线:歌单子项没有图标字形,而图标栏模板只画图标 ——
/// 它们会渲染成"看不见、但 40px 高、仍能点仍能导航"的空行。截图看不出来,节点总数也只是
/// "多了几行",只有直接数图标栏自己的行才暴露。对照(同轮实测):11 行/其中 4 行无字形 → 7 行/0 行。
///
/// ⚠ 别改用命中测试来证明"点不到":无头窗口的 `InputHitTest` 对**已知可见**的行同样返回 null
/// (实测自检),两边读数都是 0,是噪声不是证据。判据落在"这一行是否存在、有没有图标字形"上。</summary>
public static class CompactNavigationProbe
{
    private static readonly string[] Expected =
        ["搜索", "个性推荐", "浏览", "私人FM", "我的收藏", "音乐云盘", "最近播放"];

    public static int Run()
    {
        var main = ServiceLocator.Get<MainViewModel>();
        var playlist = main.Playlist;

        // 不依赖在线登录:离线歌单快照同样会进导航(与 --accountlike 同类夹具)
        playlist.IsLoggedIn = false;
        playlist.IsQqLoggedIn = false;
        playlist.Playlists.Clear();
        playlist.QqPlaylists.Clear();
        playlist.Playlists.Add(Item(801, "回归歌单 A", MusicSource.NetEase));
        playlist.Playlists.Add(Item(802, "回归歌单 B", MusicSource.NetEase));
        playlist.QqPlaylists.Add(Item(803, "回归歌单 C", MusicSource.QQ));
        Drain();

        var children = main.ShellNavItems.Where(i => i.IsPlaylistChild).ToArray();
        var allItemRows = main.ShellNavItems.Count(i => i.IsItem);
        Console.WriteLine($"[compact-nav] 夹具:侧栏歌单子项={children.Length},"
            + $"图标栏若取\"全部 IsItem 行\"则有 {allItemRows} 行(其中无字形空行={children.Length} ← 修复前的形态)");

        var window = new Window { Width = 1200, Height = 720 };
        var shell = new AppShell { DataContext = main };
        window.Content = shell;
        window.Show();
        Drain();

        // 宽屏 + 未展开 = 内联图标栏(WideSidebar 收起、CompactSidebar 顶上来)
        main.IsNavigationExpanded = false;
        Drain();

        var wide = shell.FindControl<Border>("WideSidebar");
        var compact = shell.FindControl<Border>("CompactSidebar")
                      ?? throw new InvalidOperationException("找不到图标栏 CompactSidebar。");
        if (wide is { IsVisible: true })
            throw new InvalidOperationException("收起后完整侧栏仍可见。");
        if (!compact.IsVisible)
            throw new InvalidOperationException("收起后图标栏没有显示。");

        var list = compact.GetVisualDescendants().OfType<ListBox>().FirstOrDefault()
                   ?? throw new InvalidOperationException("图标栏里找不到导航 ListBox。");

        var labels = list.ItemsSource!.Cast<NavItemViewModel>().Select(i => i.Label).ToArray();
        var rows = list.GetVisualDescendants().OfType<ListBoxItem>()
            .Where(r => r.DataContext is NavItemViewModel).ToArray();
        var blank = rows.Where(r => r.DataContext is NavItemViewModel n
                                     && string.IsNullOrEmpty(n.IconGlyph)).ToArray();
        var playlistRows = rows.Where(r => r.DataContext is NavItemViewModel { IsPlaylistChild: true }).ToArray();

        Console.WriteLine($"[compact-nav] 图标栏绑定项=[{string.Join(",", labels)}]");
        Console.WriteLine($"[compact-nav] 图标栏实化行={rows.Length},无字形行={blank.Length}(期望 0),"
            + $"歌单子项行={playlistRows.Length}(期望 0)");

        // "看不见却能点"是"图标栏里存在无字形的行"的推论:图标栏模板只画图标,无字形行在屏幕上
        // 是空白,而它照样是 ListBoxItem —— 同样响应指针、同样被 SelectionChanged 换成一次导航。
        // 所以这一段的判据就是:图标栏里不许有"无字形的行"。实测 11 行/4 空 → 7 行/0 空。
        var failed = new List<string>();
        if (!labels.SequenceEqual(Expected))
            failed.Add($"图标栏项=[{string.Join(",", labels)}],期望=[{string.Join(",", Expected)}]");
        if (playlistRows.Length > 0)
            failed.Add($"图标栏里仍有 {playlistRows.Length} 个歌单子项行(无图标,渲染成看不见却可点的空行)");
        if (blank.Length > 0)
            failed.Add($"图标栏里有 {blank.Length} 行没有图标字形,屏幕上是一片可点的空白");

        // ---- 底部入口(账号/设置/Debug)的左右留白:必须和上方导航行一致 ----
        // 上方行的留白来自 `ListBox.shell-navigation > ListBoxItem { Margin="4,0" }`(AppShell.axaml);
        // 底部按钮是普通 Button,不吃这条样式,留白只能在各自的 XAML 里写死 —— 于是两处都写歪了:
        //   图标栏(AppShell.axaml)     StackPanel Margin="0,0,0,8" → 左右 0,按钮贴边
        //   展开态(NavigationPaneView) StackPanel Margin="4,0,8,8" → 左 4 右 8,不对称
        // 量的是**渲染后的留白**(用户实际看到的),与该形态首行的读数比 —— 两者必须完全相同。
        void MeasureBottom(string tag, Visual host, ListBox navList, IEnumerable<Button> buttons)
        {
            (double Left, double Right) Edges(Visual v)
            {
                var tl = v.TranslatePoint(new Point(0, 0), host) ?? default;
                var br = v.TranslatePoint(new Point(v.Bounds.Width, 0), host) ?? default;
                return (tl.X, host.Bounds.Width - br.X);
            }

            var firstRow = navList.GetVisualDescendants().OfType<ListBoxItem>().FirstOrDefault();
            var visible = buttons.Where(b => b.IsVisible).ToArray();
            if (firstRow is null || visible.Length == 0)
            {
                failed.Add($"{tag}:量不到导航行或底部入口按钮(行={firstRow is not null},按钮={visible.Length})");
                return;
            }

            var (rowLeft, rowRight) = Edges(firstRow);
            var bottom = visible.Select(Edges).ToArray();
            Console.WriteLine($"[compact-nav] {tag} 上方首行留白 左{rowLeft:F0}/右{rowRight:F0};"
                + $"底部 {visible.Length} 个入口按钮 左{string.Join("/", bottom.Select(b => b.Left.ToString("F0")))}"
                + $"/右{string.Join("/", bottom.Select(b => b.Right.ToString("F0")))}");
            foreach (var (left, right) in bottom)
                if (Math.Abs(left - rowLeft) > 0.5 || Math.Abs(right - rowRight) > 0.5)
                    failed.Add($"{tag}:底部入口留白 左{left:F0}/右{right:F0}"
                        + $" ≠ 上方导航行 左{rowLeft:F0}/右{rowRight:F0}");
        }

        MeasureBottom("图标栏(收起)", compact, list,
            compact.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("icon-button")));

        main.IsNavigationExpanded = true;
        Drain();
        var pane = wide.GetVisualDescendants().OfType<NavigationPaneView>().FirstOrDefault();
        var wideList = pane?.GetVisualDescendants().OfType<ListBox>().FirstOrDefault();
        if (pane is null || wideList is null)
            failed.Add("展开态:找不到 NavigationPaneView 或其中的导航列表");
        else
            MeasureBottom("完整侧栏(展开)", wide, wideList,
                pane.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("navigation-button")));

        window.Close();
        Drain();
        playlist.Playlists.Clear();
        playlist.QqPlaylists.Clear();
        Drain();

        if (failed.Count > 0)
        {
            foreach (var f in failed) Console.Error.WriteLine($"[compact-nav] FAIL {f}");
            return 1;
        }

        Console.WriteLine("[compact-nav] PASS 图标栏只留 7 个静态入口,没有不可见却可点的行");
        return 0;
    }

    private static PlaylistItemViewModel Item(long id, string name, MusicSource source) =>
        new(new Playlist { Id = id, Name = name, Source = source });

    private static void Drain()
    {
        for (var i = 0; i < 8; i++) Dispatcher.UIThread.RunJobs();
    }
}
