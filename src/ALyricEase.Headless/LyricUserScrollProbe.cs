using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Models.Dtos;
using ALyricEase.Services;
using ALyricEase.ViewModels;
using ALyricEase.Views;

namespace ALyricEase.Headless;

/// <summary>歌词手动滚动暂停自动跟随回归(--lyrscroll):全生产流程复现。
/// AppShell + NowPlaying 覆盖层 + detail 歌词面板(与 LyricSwitchProbe 同骨架),
/// 滚轮走 HeadlessWindowExtensions.MouseWheel 真实输入管线(WM_MOUSEWHEEL 同路径),
/// 切句走 LyricViewModel.UpdatePosition 全生产路径。
/// 断言:滚轮打标生效 → 4 秒内下一句变化不拉回 → 过期后恢复跟随。</summary>
internal static class LyricUserScrollProbe
{
    public static int Run()
    {
        var window = new Window { Width = 1200, Height = 720, Title = "lyric-userscroll" };
        var root = new Grid { RowDefinitions = new Avalonia.Controls.RowDefinitions("48,*") };
        var shell = new AppShell { DataContext = ServiceLocator.Get<MainViewModel>() };
        Grid.SetRow(shell, 1);
        root.Children.Add(shell);
        var overlay = new Grid { Name = "NowPlayingOverlay", ZIndex = 100, ClipToBounds = true };
        overlay.DataContext = shell.DataContext;
        overlay.Children.Add(new NowPlayingView());
        Grid.SetRowSpan(overlay, 2);
        root.Children.Add(overlay);
        window.Content = root;
        window.Show();
        Drain(); RunFrames(8);

        var vm = (MainViewModel)shell.DataContext!;
        vm.OpenNowPlayingCommand.Execute(null);
        Drain(); RunFrames(8);
        vm.ToggleLyricsPanelCommand.Execute(null); // 打开歌词面板
        Drain(); RunFrames(8);

        var song = new Song { Id = 11, Name = "A歌", Source = (MusicSource)99, DurationMs = 260_000 };
        long playheadMs = 0;
        var ticking = true;
        using var ticker = new Timer(_ => Dispatcher.UIThread.Post(() =>
        {
            if (ticking) vm.Lyric.UpdatePosition(Interlocked.Read(ref playheadMs));
        }), null, 0, 60);

        vm.Player.CurrentSong = song;
        Interlocked.Exchange(ref playheadMs, 100_000);
        _ = vm.Lyric.LoadAsync(song);
        WaitForLyrics(vm, 80);
        RunFrames(30); // 居中补间落定

        var lyricView = window.GetVisualDescendants().OfType<LyricView>().First();
        var list = lyricView.FindControl<ListBox>("LyricList")!;
        var scroll = list.GetVisualDescendants().OfType<ScrollViewer>().First();
        var point = list.TranslatePoint(
            new Point(list.Bounds.Width / 2, list.Bounds.Height / 2), window)!.Value;

        var index0 = list.SelectedIndex;
        var offsetA = scroll.Offset.Y;

        // ── 1. 真实滚轮:列表滚动 + 打标生效 ─────────────────────────────
        window.MouseWheel(point, new Vector(0, -300), RawInputModifiers.None);
        RunFrames(4);
        var offsetB = scroll.Offset.Y;
        var stamp = ReadUserScrollStamp(lyricView);
        Console.WriteLine($"[lyrscroll] 滚轮: 选中={index0} Offset {offsetA:F1} → {offsetB:F1} 打标={stamp} (期望滚动且打标>0)");

        // ── 2. 暂停期内下一句变化:不许拉回 ─────────────────────────────
        Interlocked.Exchange(ref playheadMs, NextLineMs(index0));
        RunFrames(12);
        var indexC = list.SelectedIndex;
        var offsetC = scroll.Offset.Y;
        var pulled = Math.Abs(offsetC - offsetB);
        Console.WriteLine($"[lyrscroll] 暂停期切句: 选中 {index0}→{indexC} Offset {offsetB:F1} → {offsetC:F1} 漂移={pulled:F1}px (期望 ≤5px)");

        // ── 3. 暂停过期(>4s)后下一句变化:恢复跟随 ──────────────────────
        Thread.Sleep(4200);
        Interlocked.Exchange(ref playheadMs, NextLineMs(indexC));
        RunFrames(24);
        var indexD = list.SelectedIndex;
        var offsetD = scroll.Offset.Y;
        var resumed = Math.Abs(offsetD - offsetC);
        Console.WriteLine($"[lyrscroll] 过期后切句: 选中 {indexC}→{indexD} Offset {offsetC:F1} → {offsetD:F1} 位移={resumed:F1}px (期望 >5px)");
        ticking = false;

        var pass = stamp > 0 && indexC != index0 && pulled <= 5 && resumed > 5;
        Console.WriteLine($"[lyrscroll] {(pass ? "PASS" : "FAIL")}");
        window.Close();
        return pass ? 0 : 1;
    }

    private static long NextLineMs(int currentIndex) => (long)(currentIndex + 2) * 3_000 + 1_100;

    /// <summary>--lyrblur:手动滚动暂停期模糊渐隐/渐显回归。
    /// 断言:① 行的 Effect 就是共享资源实例(样式 Setter 引用同一对象 ⇒ 改 Radius 全体生效);
    /// ② 滚轮后共享半径渐变到 0 且 350ms 后整档摘 Effect(blur-suspended);
    /// ③ 4 秒到了但没切句(没拉回)⇒ 模糊保持摘除,**不**按固定 4 秒恢复;
    /// ④ 切句真正拉回(Recenter)⇒ 摘类、半径渐回档位值、Effect 重新挂上。
    /// 切句全部手动驱动 UpdatePosition(无 ticker),保证时间点可控。</summary>
    public static int RunBlur()
    {
        var (window, shell) = OpenLyricPanel();
        var vm = (MainViewModel)shell.DataContext!;
        try
        {
            var song = new Song { Id = 11, Name = "A歌", Source = (MusicSource)99, DurationMs = 260_000 };
            vm.Player.CurrentSong = song;
            _ = vm.Lyric.LoadAsync(song);
            WaitForLyrics(vm, 80);
            vm.Lyric.UpdatePosition(100_000); // 第 33 句
            RunFrames(30);

            var lyricView = window.GetVisualDescendants().OfType<LyricView>().First();
            var list = lyricView.FindControl<ListBox>("LyricList")!;
            var far = (BlurEffect)lyricView.Resources["LyricBlurFar"]!;
            var above = (BlurEffect)lyricView.Resources["LyricBlurAbove"]!;
            var below1 = (BlurEffect)lyricView.Resources["LyricBlurBelow1"]!;
            var below2 = (BlurEffect)lyricView.Resources["LyricBlurBelow2"]!;
            var shared = new BlurEffect[] { far, above, below1, below2 };

            var item = list.GetVisualDescendants().OfType<ListBoxItem>()
                .FirstOrDefault(i => i.Effect is not null)
                ?? throw new InvalidOperationException("找不到带模糊的歌词行。");
            var sharedAtStart = shared.Contains(item.Effect);
            Console.WriteLine($"[lyrblur] 行 Effect 是共享实例={sharedAtStart} (期望 True;False=样式各自实例,渐变方案不成立)");

            var point = list.TranslatePoint(
                new Point(list.Bounds.Width / 2, list.Bounds.Height / 2), window)!.Value;
            window.MouseWheel(point, new Vector(0, -300), RawInputModifiers.None);
            RunFrames(24); // ~600ms:350ms 渐变 + 50ms 后挂类

            var radiiZero = shared.All(e => e.Radius < 0.05);
            var suspendedOn = lyricView.Classes.Contains("blur-suspended");
            var effectCleared = item.Effect is null;
            Console.WriteLine($"[lyrblur] 滚轮后: 半径={Fmt(shared)} suspended={suspendedOn} 行Effect为null={effectCleared} (期望 全0/True/True)");

            // ── 4 秒到了但没切句 ⇒ 模糊必须保持摘除(这是本次需求的核心语义)──
            Thread.Sleep(4300); // 阻塞 UI 线程;期间无任何 UpdatePosition ⇒ 不会拉回
            RunFrames(4);
            var heldAfter4s = lyricView.Classes.Contains("blur-suspended") && shared.All(e => e.Radius < 0.05);
            Console.WriteLine($"[lyrblur] 4s 无切句: 模糊仍摘除={heldAfter4s} (期望 True;False=仍在按固定 4 秒恢复)");

            // ── 切句 → 自动跟随拉回(Recenter)→ 模糊渐变回来 ──
            vm.Lyric.UpdatePosition(106_100); // 第 35 句
            // 渐变回档位值:headless 时钟逐帧推进,给足帧数等半径到位(0.35s 渐变)
            var radiiBack = false;
            string? midSample = null;
            for (var i = 0; i < 160 && !radiiBack; i++)
            {
                Thread.Sleep(25);
                // ⚠ headless 没有持续渲染泵:Transition 走「帧渲染才推进时钟」的路径,不补帧会永远停在起步值
                // (实测卡在 ~1% 不动;真实应用有 UiFramePacer 连续出帧,不存在此问题)。这里每轮补一帧等效真机。
                lyricView.InvalidateVisual();
                Dispatcher.UIThread.RunJobs();
                if (i == 4) midSample = Fmt(shared);
                radiiBack = Math.Abs(far.Radius - 5) < 0.3 && Math.Abs(above.Radius - 1.5) < 0.3
                    && Math.Abs(below1.Radius - 1) < 0.3 && Math.Abs(below2.Radius - 2.5) < 0.3;
            }

            var suspendedOff = !lyricView.Classes.Contains("blur-suspended");
            var effectBack = list.GetVisualDescendants().OfType<ListBoxItem>()
                .Any(i => i.Effect is not null && shared.Contains(i.Effect));
            Console.WriteLine($"[lyrblur] 拉回后: 渐变中样本={midSample ?? "-"} 最终半径={Fmt(shared)} " +
                $"类已摘除={suspendedOff} 行Effect已挂回={effectBack} (期望 渐变过程/档位值/True/True)");

            var pass = sharedAtStart && radiiZero && suspendedOn && effectCleared
                && heldAfter4s && radiiBack && suspendedOff && effectBack;
            Console.WriteLine($"[lyrblur] {(pass ? "PASS" : "FAIL")}");
            window.Close();
            return pass ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[lyrblur] FAIL {ex}");
            window.Close();
            return 1;
        }
    }

    private static string Fmt(BlurEffect[] effects)
        => string.Join("/", effects.Select(e => e.Radius.ToString("F2")));

    /// <summary>探针共用骨架:窗口 + AppShell + NowPlaying 覆盖层 + 打开歌词面板。</summary>
    private static (Window Window, AppShell Shell) OpenLyricPanel()
    {
        var window = new Window { Width = 1200, Height = 720, Title = "lyric-probe" };
        var root = new Grid { RowDefinitions = new Avalonia.Controls.RowDefinitions("48,*") };
        var shell = new AppShell { DataContext = ServiceLocator.Get<MainViewModel>() };
        Grid.SetRow(shell, 1);
        root.Children.Add(shell);
        var overlay = new Grid { Name = "NowPlayingOverlay", ZIndex = 100, ClipToBounds = true };
        overlay.DataContext = shell.DataContext;
        overlay.Children.Add(new NowPlayingView());
        Grid.SetRowSpan(overlay, 2);
        root.Children.Add(overlay);
        window.Content = root;
        window.Show();
        Drain(); RunFrames(8);

        var vm = (MainViewModel)shell.DataContext!;
        vm.OpenNowPlayingCommand.Execute(null);
        Drain(); RunFrames(8);
        vm.ToggleLyricsPanelCommand.Execute(null);
        Drain(); RunFrames(8);
        return (window, shell);
    }

    private static long ReadUserScrollStamp(LyricView view)
        => (long)(typeof(LyricView)
            .GetField("_lastUserScrollTimestamp", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(view) ?? 0L);

    private static void WaitForLyrics(MainViewModel vm, int maxFrames)
    {
        for (var i = 0; i < maxFrames && vm.Lyric.Lines.Count == 0; i++)
        {
            Thread.Sleep(25);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void RunFrames(int frames)
    {
        for (var i = 0; i < frames; i++)
        {
            Thread.Sleep(25);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void Drain()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
    }
}
