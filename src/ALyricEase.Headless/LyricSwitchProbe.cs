using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Models.Dtos;
using ALyricEase.Services;
using ALyricEase.ViewModels;
using ALyricEase.Views;

namespace ALyricEase.Headless;

/// <summary>离线假音源(Source=99,避开真实网易云/QQ 注册):可播地址 + 带歌曲标记的歌词。
/// 供 LyricSwitchProbe 端到端驱动 Player.PlayAsync/LyricViewModel.LoadAsync 全生产路径。</summary>
internal sealed class OfflineProbeApi : IMusicApi
{
    public MusicSource Source => (MusicSource)99;
    public string DisplayName => "Probe";
    public bool IsLoggedIn => true;
    public bool IsVip => false;
    public bool IsVipLoaded => true;
    public Task EnsureVipStatusAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task<List<Song>> SearchAsync(string keyword, int limit = 30, int offset = 0, CancellationToken ct = default)
        => Task.FromResult(new List<Song>());
    public Task<PlayUrlItem?> GetPlayUrlAsync(Song song, string level = "higher", CancellationToken ct = default)
        => Task.FromResult<PlayUrlItem?>(new PlayUrlItem { Id = song.Id, Url = $"probe://audio/{song.Id}", IsTrial = true });

    public Task<LyricResult?> GetLyricAsync(Song song, CancellationToken ct = default)
    {
        var lines = song.Id switch { 11 => 80, 22 => 60, 33 => 40, _ => 50 };
        var marker = $"歌{song.Id}";
        var sb = new StringBuilder();
        for (var i = 0; i < lines; i++)
        {
            var t = TimeSpan.FromSeconds(i * 3 + 1);
            sb.Append('[').Append(t.ToString(@"mm\:ss\.ff")).Append(']')
                .Append(marker).Append("·第").Append(i + 1).Append("句\n");
        }
        // 模拟真实网络乱序:不同歌曲延迟不同(歌11 慢 400ms,其余快/中)
        var delay = song.Id switch { 11 => 400, 22 => 40, 33 => 180, _ => 120 };
        return Task.Run(async () =>
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
            return (LyricResult?)new LyricResult { Original = sb.ToString() };
        }, ct);
    }

    public Task<Song?> GetSongDetailAsync(long id, CancellationToken ct = default) => Task.FromResult<Song?>(null);
}

/// <summary>--lyricswitch:切歌后歌词容器残留旧词复现探针(端到端)。
/// 结构与 MainWindow 一致(AppShell + 窗口级 NowPlaying 覆盖层),歌词面板真实打开;
/// 切歌走 Player.PlayAsync 全生产路径(PrepareSongPlayback→LyricViewModel.LoadAsync)。
/// 变体:A 稳定播放滚动到中部后切 B / 滚动居中动画进行中切歌 / 快速连切 / 切歌前后开关面板。
/// 稳定后检查 LyricList:ItemCount、虚拟化面板子项、异主容器(DataContext 不在当前 Lines)、
/// 旧歌标记文本残留、滚动偏移是否超出当前内容范围。</summary>
public static class LyricSwitchProbe
{
    public static void Run()
    {
        var window = new Window { Width = 1200, Height = 720, Title = "lyric-switch-np" };
        var root = new Grid { RowDefinitions = new Avalonia.Controls.RowDefinitions("48,*") };
        var shell = new AppShell { DataContext = ServiceLocator.Get<MainViewModel>() };
        Grid.SetRow(shell, 1);
        root.Children.Add(shell);
        var overlay = new Grid { Name = "NowPlayingOverlay", ZIndex = 100, ClipToBounds = true };
        overlay.DataContext = shell.DataContext; // MainWindow 中 NowPlayingView DataContext="{Binding}",此处等效
        overlay.Children.Add(new NowPlayingView());
        Grid.SetRow(overlay, 0);
        Grid.SetRowSpan(overlay, 2);
        root.Children.Add(overlay);
        window.Content = root;
        window.Show();
        Drain();

        var vm = (MainViewModel)shell.DataContext!;
        vm.OpenNowPlayingCommand.Execute(null);
        Drain(); RunFrames(8);
        vm.ToggleLyricsPanelCommand.Execute(null); // 打开歌词面板
        Drain(); RunFrames(8);

        var songA = new Song { Id = 11, Name = "A歌", Source = (MusicSource)99, DurationMs = 260_000 };
        var songB = new Song { Id = 22, Name = "B歌", Source = (MusicSource)99, DurationMs = 200_000 };
        var songC = new Song { Id = 33, Name = "C歌", Source = (MusicSource)99, DurationMs = 140_000 };

        // ── 场景1:A 稳定播放滚动到中部 → 切 B ─────────────────────────────
        // 说明:不走 Player.PlayAsync(AudioQualityMapper 对假音源 99 抛异常,且主 provider
        // 无法注册重复 NetEase 键);歌词切换组件本身就是 LyricViewModel.LoadAsync,
        // 这里直接驱动它并同步 Player 状态,宿主/面板/动画全部真实。
        // 真实播放中位置回调 ~3-4 次/秒持续驱动高亮:探针全程开一个 ticker 模拟
        long playheadMs = 0;
        var ticking = true;
        using var ticker = new Timer(_ => Dispatcher.UIThread.Post(() =>
        {
            if (ticking) vm.Lyric.UpdatePosition(Interlocked.Read(ref playheadMs));
        }), null, 0, 60);

        // ── 场景1:A 稳定播放(ticker 持续驱动高亮/滚动)→ 切 B ─────────────
        vm.Player.CurrentSong = songA;
        Interlocked.Exchange(ref playheadMs, 100_000);
        _ = vm.Lyric.LoadAsync(songA);
        // 歌词回填,ticker 同时驱动高亮/滚动。
        // 必须**等到歌词真的到达**再继续:OfflineProbeApi 对歌 11 有 400ms 延迟,
        // 而 RunFrames(n) 每帧只 Sleep(25ms) ⇒ 原本的 RunFrames(10)≈250ms 是边界竞态
        // (Windows 上 Sleep 实际常 30~40ms,连跑多个探针后机器一忙就不够)。
        // 量到 Lines=0 时,后面按 LyricList 取 ScrollViewer 那步会 First() 抛
        // "Sequence contains no elements",表现为**假失败**。
        WaitForLyrics(vm, 80);
        Dump("场景1·A播放中", window, vm);
        Shot(window, "aly-switch-1-a");

        vm.Player.CurrentSong = songB;
        Interlocked.Exchange(ref playheadMs, 50_000);
        _ = vm.Lyric.LoadAsync(songB);
        RunFrames(40);
        Dump("场景1·B已切换", window, vm);
        Shot(window, "aly-switch-1-b");

        // ── 场景2:用户手动滚轮深滚到底后立即切歌 ─────────────────────────
        var lyricList = window.GetVisualDescendants().OfType<LyricView>().First()
            .FindControl<ListBox>("LyricList")!;
        var wheelScroll = lyricList.GetVisualDescendants().OfType<ScrollViewer>().First();
        wheelScroll.Offset = wheelScroll.Offset.WithY(wheelScroll.Extent.Height);
        RunFrames(6);
        vm.Player.CurrentSong = songC;
        Interlocked.Exchange(ref playheadMs, 20_000);
        _ = vm.Lyric.LoadAsync(songC);
        RunFrames(2); // 布局未稳时新词就到了
        RunFrames(40);
        Dump("场景2·手动深滚后切歌", window, vm);

        // ── 场景3:滚动动画进行中切歌 + 快速连切 ──────────────────────────
        vm.Player.CurrentSong = songA;
        Interlocked.Exchange(ref playheadMs, 150_000);
        _ = vm.Lyric.LoadAsync(songA);
        RunFrames(4); // ~100ms,居中动画进行到一半
        _ = vm.Lyric.LoadAsync(songC);
        Drain(); RunFrames(2);
        _ = vm.Lyric.LoadAsync(songB);
        Drain();
        RunFrames(40);
        Dump("场景3·动画中连切", window, vm);

        // ── 场景4:切歌前后开关面板(300ms 滑出动画期间切歌再滑入) ─────────
        vm.ToggleLyricsPanelCommand.Execute(null); // 关面板
        RunFrames(4); // 滑出动画进行中
        _ = vm.Lyric.LoadAsync(songC);
        Drain();
        vm.ToggleLyricsPanelCommand.Execute(null); // 再开面板
        RunFrames(40);
        vm.Player.SeekTo(30_000);
        RunFrames(40);
        Dump("场景4·面板循环", window, vm);
        // ── 场景5:乱序到达 + 极速连切(歌11 最慢,最后发却最先回来之前不结束)──
        // 真实网络下连续跳歌时,前一首的词可能比当前首更晚/更早到达。
        vm.Player.CurrentSong = songA;
        Interlocked.Exchange(ref playheadMs, 100_000);
        _ = vm.Lyric.LoadAsync(songA);
        RunFrames(3);
        _ = vm.Lyric.LoadAsync(songB);
        RunFrames(3);
        _ = vm.Lyric.LoadAsync(songC);
        RunFrames(3);
        _ = vm.Lyric.LoadAsync(songA); // 慢请求再次在途
        RunFrames(60); // 等全部在途请求落地(歌11 慢,最后到达)
        Dump("场景5·乱序连切", window, vm);
        Shot(window, "aly-switch-5-race");
        ticking = false;

        window.Close();
        Console.WriteLine("[switch] done");
    }

    private static void Dump(string stage, Window window, MainViewModel vm)
    {
        var lyricView = window.GetVisualDescendants().OfType<LyricView>().FirstOrDefault();
        var list = lyricView?.FindControl<ListBox>("LyricList");
        if (list is null)
        {
            Console.WriteLine($"[{stage}] 找不到 LyricList");
            return;
        }
        var lines = vm.Lyric.Lines;
        var marker = lines.Count > 0 ? lines[0].Original.Split('·')[0] : "歌0";
        var scroll = list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        var panel = list.GetVisualDescendants().OfType<VirtualizingStackPanel>().FirstOrDefault();
        var containers = list.GetVisualDescendants().OfType<ListBoxItem>().ToList();
        var foreign = containers
            .Select(c => c.DataContext as LyricLine)
            .Where(line => line is not null && !lines.Contains(line))
            .ToList();
        var texts = list.GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Text).Where(s => !string.IsNullOrEmpty(s) && s.Contains('·')).ToList();
        var stale = texts.Where(t => !t.StartsWith(marker)).ToList();
        var overScroll = scroll is { } s && s.Extent.Height > 0 && s.Offset.Y > s.Extent.Height - s.Viewport.Height + 0.5;

        Console.WriteLine($"[{stage}] Lines={lines.Count} HasLyric={vm.Lyric.HasLyric} CurrentIndex={vm.Lyric.CurrentIndex} " +
            $"ItemCount={list.ItemCount} ListVisible={list.IsVisible} Offset.Y={scroll?.Offset.Y:F0} " +
            $"Extent={scroll?.Extent.Height:F0} VSP={panel?.Children.Count} realized={containers.Count}");
        Console.WriteLine($"    异主容器={foreign.Count} 旧词残留={stale.Count} 偏移越界={overScroll}");
        foreach (var t in stale.Take(4)) Console.WriteLine($"    残留: {t}");
        foreach (var c in foreign.Take(4)) Console.WriteLine($"    异主: {c.Original}");
    }

    private static void Shot(Window window, string name)
    {
        var px = new PixelSize((int)window.ClientSize.Width, (int)window.ClientSize.Height);
        using var rtb = new RenderTargetBitmap(px, new Vector(96, 96));
        rtb.Render(window);
        var path = Path.Combine(Path.GetTempPath(), name + ".png");
        rtb.Save(path);
        Console.WriteLine($"    截图: {path}");
    }

    private static void Drain()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
    }

    // ── 真窗口变体(--lyricswitch-real):真实合成器下检测切歌后旧词"叠在上面"的重影 ──
    // 方法:切歌稳定后同时取两份像素 —— GDI BitBlt 抓屏幕实际呈现帧(合成器输出)
    // 与 RenderTargetBitmap 重渲染当前可视树;两者有显著差异 → 合成器残留旧图层。
    // 反复切歌 + 期间开关歌词面板/整个覆盖层加压,任何一轮出现差异即复现。

    public static async Task RunRealAsync()
    {
        var window = new Window
        {
            Width = 1200,
            Height = 720,
            Title = "lyric-switch-real",
            ShowActivated = false,
            WindowDecorations = WindowDecorations.None,
        };
        var root = new Grid { RowDefinitions = new Avalonia.Controls.RowDefinitions("48,*") };
        var shell = new AppShell { DataContext = ServiceLocator.Get<MainViewModel>() };
        Grid.SetRow(shell, 1);
        root.Children.Add(shell);
        var overlay = new Grid { Name = "NowPlayingOverlay", ZIndex = 100, ClipToBounds = true };
        overlay.DataContext = shell.DataContext;
        overlay.Children.Add(new NowPlayingView());
        Grid.SetRow(overlay, 0);
        Grid.SetRowSpan(overlay, 2);
        root.Children.Add(overlay);
        window.Content = root;
        window.Show();
        await Task.Delay(300);

        var vm = (MainViewModel)shell.DataContext!;
        vm.OpenNowPlayingCommand.Execute(null);
        await Task.Delay(600);
        vm.ToggleLyricsPanelCommand.Execute(null); // 打开歌词面板
        await Task.Delay(600);

        var songs = new[]
        {
            new Song { Id = 11, Name = "A歌", Source = (MusicSource)99, DurationMs = 260_000 },
            new Song { Id = 22, Name = "B歌", Source = (MusicSource)99, DurationMs = 200_000 },
            new Song { Id = 33, Name = "C歌", Source = (MusicSource)99, DurationMs = 140_000 },
        };

        long playheadMs = 0;
        var ticking = true;
        using var ticker = new Timer(_ => Dispatcher.UIThread.Post(() =>
        {
            if (ticking) vm.Lyric.UpdatePosition(Interlocked.Read(ref playheadMs));
        }), null, 0, 60);

        var worst = 0;
        var worstName = "";
        for (var cycle = 0; cycle < 8; cycle++)
        {
            var song = songs[cycle % songs.Length];
            vm.Player.CurrentSong = song;
            Interlocked.Exchange(ref playheadMs, 80_000 + cycle * 20_000);
            _ = vm.Lyric.LoadAsync(song);
            await Task.Delay(140); // 加载在途

            if (cycle % 3 == 2)
            {
                vm.ToggleLyricsPanelCommand.Execute(null); // 面板关
                await Task.Delay(180);
                vm.ToggleLyricsPanelCommand.Execute(null); // 面板开
            }
            if (cycle % 4 == 3)
            {
                vm.CloseNowPlayingCommand.Execute(null); // 整个覆盖层收起(0.4s 滑出)
                await Task.Delay(460);
                vm.OpenNowPlayingCommand.Execute(null);
            }

            await Task.Delay(1400); // 动画与滚动全部稳定
            var diff = await CaptureDiff(window, $"real-cycle{cycle}-song{song.Id}");
            if (diff > worst) { worst = diff; worstName = $"cycle{cycle}-song{song.Id}"; }
            Console.WriteLine($"[real] 轮{cycle} → 歌{song.Id}: 稳定帧vs强制重渲染 差异像素={diff}");
        }
        ticking = false;
        Console.WriteLine($"[real] 最大差异={worst} ({worstName}) (预期 0~极小;大量差异=合成器残留旧图层)");
        Console.WriteLine($"[real] 截图目录: {Path.GetTempPath()}");
        await Task.Delay(100);
        window.Close();
    }

    /// <summary>抓当前呈现帧(GDI)→ 强制整窗重渲染(尺寸±1 触发图层重建)→ 再抓一帧,统计差异。
    /// 稳定后两帧本应一致;差异显著说明合成器正在呈现与当前可视树不符的旧内容。</summary>
    private static async Task<int> CaptureDiff(Window window, string name)
    {
        var beforeFrame = ScreenCapture.GrabClient(window);
        if (beforeFrame.IsEmpty) return -1;

        // 强制整窗重渲染:尺寸抖动 1px 触发完整布局与图层重建
        window.Width = window.Width + 1;
        await Task.Delay(60);
        window.Width = window.Width - 1;
        await Task.Delay(500); // 布局/过渡重新稳定

        var afterFrame = ScreenCapture.GrabClient(window);
        var ratio = ScreenCapture.DiffRatio(beforeFrame, afterFrame, null, 28);

        ScreenCapture.Save(beforeFrame, $"real-{name}-before");
        ScreenCapture.Save(afterFrame, $"real-{name}-after");

        // 调用方按"差异像素个数"设阈值,这里换回绝对个数而不是比例。
        return double.IsNaN(ratio)
            ? -1
            : (int)Math.Round(ratio * beforeFrame.Width * beforeFrame.Height);
    }

    /// <summary>等歌词真正到达再继续(见调用点注释:固定帧数 RunFrames 对异步回填是边界竞态)。
    /// 上限 maxFrames 帧只是保险,超时也继续,不改变探针语义。</summary>
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
}
