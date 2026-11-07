using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using ALyricEase.Infrastructure;
using ALyricEase.ViewModels;
using ALyricEase.Views;

namespace ALyricEase.Headless;

/// <summary>回归:详情页的歌词/播放列表面板不许把"返回"吃掉,也不许被返回链顺手清掉。
/// 桌面鼠标侧键走 <c>TryHandleBack(includeNowPlayingPanels: false)</c> —— 侧键 = 退一层页面,
/// 一次就把整个详情页收起,面板状态原样保留(下次进来还是上次的样子);
/// 面板可以留在 VM 里跟着详情页,但它在别的页面**不能**再当一层来消费返回。</summary>
public static class NpBackProbe
{
    public static void Run()
    {
        var main = ServiceLocator.Get<MainViewModel>();
        var pass = true;

        // ① 详情页 + 歌词面板:侧键一次收起整个详情页,面板状态不动
        main.ActivePage = "Search";
        main.OpenNowPlayingCommand.Execute(null);
        main.ToggleLyricsPanelCommand.Execute(null);
        var handled = main.TryHandleBack(includeNowPlayingPanels: false);
        pass &= Check("① 侧键一次收起详情页(面板状态保留)",
            handled && !main.ShowNowPlaying && main.NowPlayingPanel == NowPlayingPanel.Lyrics);

        // ② 重新打开详情页:面板还是上次开着的那个,不"自己收起"
        main.OpenNowPlayingCommand.Execute(null);
        pass &= Check("② 重开详情页面板仍在",
            main.ShowNowPlaying && main.NowPlayingPanel == NowPlayingPanel.Lyrics);

        // ③ 关键回归:面板留在 VM 里,但在别的页面按侧键必须真的退一层,不能先"关面板"空消费一次
        main.CloseNowPlayingCommand.Execute(null);
        var before = main.ActivePage;
        main.TryHandleBack(includeNowPlayingPanels: false);
        pass &= Check($"③ 其他页面返回正常退层({before} → {main.ActivePage})",
            main.ActivePage != before && main.NowPlayingPanel == NowPlayingPanel.Lyrics);

        // ④ Android 语义(default)也一样:详情页没开时残留面板不许吃返回
        main.ActivePage = "Recommend";
        main.TryHandleBack();
        pass &= Check($"④ 详情页关闭时残留面板不吃返回({main.ActivePage})",
            main.ActivePage != "Recommend" && main.NowPlayingPanel == NowPlayingPanel.Lyrics);

        // ⑤ Android 语义保留:详情页开着时手势先收面板,页面还在
        main.OpenNowPlayingCommand.Execute(null);
        main.ToggleQueuePanelCommand.Execute(null);
        pass &= Check("⑤ Android 返回先收面板",
            main.TryHandleBack() && main.ShowNowPlaying
            && main.NowPlayingPanel == NowPlayingPanel.None);

        // ⑥ 界面侧:带着歌词面板收起详情页,再打开必须还是开着的(用户报的"下次自己收起")
        var view = new NowPlayingView();
        var window = new Window { Width = 1200, Height = 720 };
        var root = new Grid();
        root.Children.Add(new AppShell { DataContext = main });
        var overlay = new Grid { ZIndex = 100, ClipToBounds = true, DataContext = main };
        overlay.Children.Add(view);
        root.Children.Add(overlay);
        window.Content = root;
        window.Show();
        Dispatcher.UIThread.RunJobs();

        main.ActivePage = "Recommend";
        main.OpenNowPlayingCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        if (main.NowPlayingPanel == NowPlayingPanel.None) main.ToggleLyricsPanelCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        main.CloseNowPlayingCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        main.OpenNowPlayingCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var lyricsPanel = view.FindControl<Grid>("LyricsPanel");
        pass &= Check("⑥ 重开详情页歌词面板仍然可见",
            main.NowPlayingPanel == NowPlayingPanel.Lyrics && lyricsPanel is { IsVisible: true });
        window.Close();

        Console.WriteLine(pass ? "[npback] 全部通过" : "[npback] 存在失败项");
        Environment.ExitCode = pass ? 0 : 1;
    }

    private static bool Check(string name, bool ok)
    {
        Console.WriteLine($"[npback] {name}: {(ok ? "通过" : "失败")}");
        return ok;
    }
}
