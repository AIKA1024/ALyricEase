using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
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
        return Task.FromResult<LyricResult?>(new LyricResult { Original = sb.ToString() });
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
        vm.Player.CurrentSong = songA;
        _ = vm.Lyric.LoadAsync(songA);
        Drain(); RunFrames(10); // 歌词经缓存/假源回填
        vm.Player.SeekTo(100_000); // 高亮第 33 句附近,列表滚到中部
        RunFrames(40); // 420ms 居中动画走完
        Dump("场景1·A播放中", window, vm);
        Shot(window, "aly-switch-1-a");

        vm.Player.CurrentSong = songB;
        _ = vm.Lyric.LoadAsync(songB);
        Drain(); RunFrames(10);
        vm.Player.SeekTo(50_000);
        RunFrames(40);
        Dump("场景1·B已切换", window, vm);
        Shot(window, "aly-switch-1-b");

        // ── 场景2:滚动动画进行中切歌 + 快速连切 B→C→A ─────────────────────
        vm.Player.SeekTo(150_000);
        RunFrames(4); // ~100ms,居中动画进行到一半
        _ = vm.Lyric.LoadAsync(songC);
        Drain(); RunFrames(2);
        _ = vm.Lyric.LoadAsync(songB);
        Drain();
        vm.Player.SeekTo(40_000);
        RunFrames(40);
        Dump("场景2·动画中连切", window, vm);

        // ── 场景3:切歌前后开关面板(300ms 滑出动画期间切歌再滑入) ─────────
        vm.ToggleLyricsPanelCommand.Execute(null); // 关面板
        RunFrames(4); // 滑出动画进行中
        _ = vm.Lyric.LoadAsync(songC);
        Drain();
        vm.ToggleLyricsPanelCommand.Execute(null); // 再开面板
        RunFrames(40);
        vm.Player.SeekTo(30_000);
        RunFrames(40);
        Dump("场景3·面板循环", window, vm);
        Shot(window, "aly-switch-3-c");

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

    private static void RunFrames(int frames)
    {
        for (var i = 0; i < frames; i++)
        {
            Thread.Sleep(25);
            Dispatcher.UIThread.RunJobs();
        }
    }
}
