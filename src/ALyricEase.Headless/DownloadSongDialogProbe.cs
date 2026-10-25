using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.ViewModels;
using ALyricEase.Views;
using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ALyricEase.Headless;

internal static class DownloadSongDialogProbe
{
    public static void Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var main = ServiceLocator.Get<MainViewModel>();
        var song = new Song { Id = 1, Name = "后来", Artist = "刘若英",
            AvailableQualityLevels = ["standard", "exhigh", "lossless", "hires"] };
        var online = SongContextMenu.Create(new Border(), song, null);
        var local = SongContextMenu.Create(new Border(), new Song { Source = MusicSource.Local }, null);
        if (!online.Items.OfType<MenuItem>().Any(x => Equals(x.Header, "下载"))
            || local.Items.OfType<MenuItem>().Any(x => Equals(x.Header, "下载")))
            throw new InvalidOperationException("下载菜单的音源可见性错误");
        foreach (var (width, height, theme) in new[] {
            (820, 740, ThemeVariant.Light), (390, 700, ThemeVariant.Dark), (320, 480, ThemeVariant.Light) })
        {
            main.IsDownloadSongDialogOpen = false;
            main.OpenDownloadSongDialogCommand.Execute(song);
            var view = new DownloadSongDialogView { DataContext = main, IsVisible = false };
            var behind = new Button { Content = "背景按钮" };
            var window = new Window { Width = width, Height = height,
                RequestedThemeVariant = theme, Content = new Grid { Children = { behind, view } } };
            window.Show();
            behind.Focus();
            view.IsVisible = true;
            Dispatcher.UIThread.RunJobs();
            var card = view.FindControl<Border>("DialogCard")!;
            // 真实打开路径：元数据在 Open 命令内同步返回，窗口显示前查询已经结束。
            // 不手动切换 IsLoadingQualities，也必须先出现查询卡片，再进入合成动画。
            if (main.DownloadSongDialog!.IsLoadingQualities
                || !view.FindControl<StackPanel>("LoadingPanel")!.IsVisible
                || view.FindControl<Grid>("DownloadPanel")!.IsVisible)
                throw new InvalidOperationException("同步返回音质时也必须先呈现查询卡片");
            var openingClock = Stopwatch.StartNew();
            while (!view.IsMorphing && openingClock.ElapsedMilliseconds < 1500) Pump(5);
            if (!view.IsMorphing || openingClock.ElapsedMilliseconds < 180)
                throw new InvalidOperationException("快速查询没有经过首帧停留及伸展动画");
            Pump(420);
            if (view.IsMorphing || !view.FindControl<Grid>("DownloadPanel")!.IsVisible)
                throw new InvalidOperationException("快速查询打开动画未正常完成");
            Console.WriteLine($"Synchronous metadata opening {width}: query card -> composition morph -> download");
            var bounds = card.Bounds;
            if (bounds.Width > width - 30 || bounds.Height > height - 30)
                throw new InvalidOperationException($"Dialog overflow {width}x{height}: {bounds}");
            // 同一卡片先显示查询状态，再通过合成背景伸展与内容淡入过渡到最终尺寸。
            var dialog = main.DownloadSongDialog!;
            var options = dialog.Qualities;
            var previousHeight = 0d;
            foreach (var count in new[] { 1, 2, 3, 4 })
            {
                dialog.IsLoadingQualities = true;
                dialog.Qualities = [];
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                if (!card.IsVisible || !view.FindControl<StackPanel>("LoadingPanel")!.IsVisible)
                    throw new InvalidOperationException("查询时应显示同一模态卡片");
                Pump(40);
                var loadingHeight = card.Bounds.Height;
                if (count == 1)
                {
                    using var loading = window.GetLastRenderedFrame();
                    loading?.Save(Path.Combine(directory, $"loading-{width}.png"), PngBitmapEncoderOptions.Default);
                }
                behind.Focus();
                if (window.FocusManager?.GetFocusedElement() != view.FindControl<Button>("LoadingCancelButton"))
                    throw new InvalidOperationException("加载时焦点应位于取消按钮");
                dialog.Qualities = options.Take(count).ToArray();
                dialog.SelectedQuality = dialog.Qualities.LastOrDefault();
                dialog.Status = "将保存到系统“下载”文件夹；已有相同音质缓存时直接保存。";
                dialog.IsLoadingQualities = false;
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                if (!view.IsMorphing || view.FindControl<Grid>("DownloadPanel")!.IsEnabled)
                    throw new InvalidOperationException("音质查询后必须启动过渡并防止误点");
                var surface = view.FindControl<Border>("DialogSurface")!;
                var scales = new List<double>();
                var clock = Stopwatch.StartNew();
                foreach (var elapsed in new[] { 20, 80, 160, 400 })
                {
                    Pump(Math.Max(1, elapsed - (int)clock.ElapsedMilliseconds));
                    var visual = ElementComposition.GetElementVisual(surface)!;
                    var server = typeof(CompositionObject).GetProperty("Server", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .GetValue(visual)!;
                    var scale = (Vector3D)server.GetType().GetProperty("Scale")!.GetValue(server)!;
                    scales.Add(scale.Y);
                    if (count == 3)
                    {
                        using var frame = window.GetLastRenderedFrame();
                        frame?.Save(Path.Combine(directory, $"morph-{width}-{elapsed}.png"), PngBitmapEncoderOptions.Default);
                    }
                }
                Console.WriteLine($"Morph {width}/{count}: {loadingHeight:F0}->{card.Bounds.Height:F0}, scale={string.Join(",", scales.Select(s => s.ToString("F3")))}");
                if (!scales.Any(s => s > loadingHeight / card.Bounds.Height + 0.01 && s < 0.99)
                    || Math.Abs(scales[^1] - 1) > 0.01 || view.IsMorphing)
                    throw new InvalidOperationException("合成帧必须包含伸展中间态并正确结束");
                if (!card.IsVisible || card.Bounds.Height > height - 30 || card.Bounds.Width > width - 30)
                    throw new InvalidOperationException($"Dialog overflow with {count} qualities: {card.Bounds}");
                if (width == 820 && card.Bounds.Height <= previousHeight)
                    throw new InvalidOperationException("桌面弹窗应随档位数紧凑布局");
                previousHeight = card.Bounds.Height;
                using var sample = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
                sample.Render(window);
                sample.Save(Path.Combine(directory, $"download-{width}-{count}-qualities.png"), PngBitmapEncoderOptions.Default);
            }
            // 关闭时中断合成动画，重新打开不残留透明度、缩放或禁用状态。
            dialog.IsLoadingQualities = true;
            window.UpdateLayout();
            dialog.IsLoadingQualities = false;
            window.UpdateLayout();
            if (!view.IsMorphing) throw new InvalidOperationException("未开始用于中断验证的过渡");
            view.IsVisible = false;
            if (view.IsMorphing || !view.FindControl<Grid>("DownloadPanel")!.IsEnabled)
                throw new InvalidOperationException("关闭时未清理过渡状态");
            view.IsVisible = true;
            Dispatcher.UIThread.RunJobs();
            // 在查询卡片的短暂停留期间关闭，再打开，旧任务不能提前释放新窗口的查询状态。
            Pump(80);
            view.IsVisible = false;
            view.IsVisible = true;
            Pump(80);
            if (!view.FindControl<StackPanel>("LoadingPanel")!.IsVisible || view.IsMorphing)
                throw new InvalidOperationException("旧打开任务干扰了重新打开的查询卡片");
            Pump(650);
            for (var i = 0; i < 12; i++)
            {
                window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
                Dispatcher.UIThread.RunJobs();
                if (window.FocusManager?.GetFocusedElement() is not Visual focus || !view.IsVisualAncestorOf(focus))
                    throw new InvalidOperationException($"Tab 焦点逃出模态窗口: {width}, step={i}, focus={window.FocusManager?.GetFocusedElement()}");
            }
            using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
            bitmap.Render(window);
            bitmap.Save(Path.Combine(directory, $"download-{width}-{theme}.png"), PngBitmapEncoderOptions.Default);
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            if (main.IsDownloadSongDialogOpen) throw new InvalidOperationException("Esc 未关闭模态窗口");
            window.Close();
        }
        CheckShellOpening(main, song);
        main.OpenDownloadSongDialogCommand.Execute(song);
        if (!main.TryHandleBack() || main.IsDownloadSongDialogOpen)
            throw new InvalidOperationException("系统返回未关闭下载窗口");
        Console.WriteLine("PASS: composition morph frames, cancellation/reopen, compact quality counts, desktop/mobile/short-screen layouts, light/dark, Tab trap, Escape, system back.");
    }

    private static void CheckShellOpening(MainViewModel main, Song song)
    {
        main.IsDownloadSongDialogOpen = false;
        var view = new DownloadSongDialogView { DataContext = main, IsVisible = false };
        using var binding = view.Bind(ModalVisibilityTransition.IsOpenProperty,
            new Binding(nameof(MainViewModel.IsDownloadSongDialogOpen)));
        var window = new Window { Width = 820, Height = 740, Content = view };
        window.Show();
        main.OpenDownloadSongDialogCommand.Execute(song);
        window.UpdateLayout();
        Pump(40);
        if (!view.FindControl<StackPanel>("LoadingPanel")!.IsVisible)
            throw new InvalidOperationException("宿主淡入期间应保留查询卡片");
        main.DownloadSongDialog!.CloseCommand.Execute(null);
        Pump(40);
        main.OpenDownloadSongDialogCommand.Execute(song);
        window.UpdateLayout();
        Pump(100);
        if (!view.FindControl<StackPanel>("LoadingPanel")!.IsVisible || view.IsMorphing)
            throw new InvalidOperationException("淡出中重开不能跳过查询卡片");
        var clock = Stopwatch.StartNew();
        while (!view.IsMorphing && clock.ElapsedMilliseconds < 1500) Pump(5);
        if (!view.IsMorphing || view.Opacity < 0.95)
            throw new InvalidOperationException("宿主淡入完成后应能看见伸展动画");
        Pump(420);
        if (view.IsMorphing || !view.FindControl<Grid>("DownloadPanel")!.IsEnabled)
            throw new InvalidOperationException("宿主快速重开后未正常恢复下载交互");
        main.DownloadSongDialog.CloseCommand.Execute(null);
        Pump(220);
        window.Close();
        Console.WriteLine("PASS: shell fade-in + synchronous metadata, close/reopen during fade-out.");
    }

    private static void Pump(int milliseconds)
    {
        var clock = Stopwatch.StartNew();
        do
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(5);
        } while (clock.ElapsedMilliseconds < milliseconds);
        Dispatcher.UIThread.RunJobs();
    }
}
