using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.ViewModels;
using ALyricEase.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;

namespace ALyricEase.Headless;

internal static class DesktopLyricProbe
{
    public static void Run()
    {
        // 贴底保存的位置必须原样还原:曾经用固定 180 当"估计高度",把贴底的位置顶到 Bottom-180。
        Check(DesktopLyricPlacement.ClampTop(922, 118, 0, 1040) == 922, "贴底位置原样还原");
        Check(DesktopLyricPlacement.ClampTop(1000, 118, 0, 1040) == 922, "底部超出时只上推到贴底");
        Check(DesktopLyricPlacement.ClampTop(-50, 118, 0, 1040) == 0, "顶部超出时下拉回工作区");
        Check(DesktopLyricPlacement.ClampTop(300, 0, 0, 1040) == 300, "高度未知时不改变合法位置");
        // 首帧前 SizeChanged 会报 (Width, Width) 的假尺寸,必须拒掉(否则 1040-720=320)。
        Check(!DesktopLyricPlacement.IsPlausibleHeight(720, 1040), "假尺寸（宽度当高度）被拒");
        Check(DesktopLyricPlacement.IsPlausibleHeight(100, 1040), "真实窗口高度可用");
        Check(!DesktopLyricPlacement.IsPlausibleHeight(0, 1040), "高度为 0 时跳过");

        var dir = Path.Combine(Path.GetTempPath(), "aly-desktop-lyrics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var state = new AppStateStore(Path.Combine(dir, "state.json"));
        HeadlessApp.ConfigureServices(services =>
        {
            services.AddSingleton(state);
            services.AddSingleton<DesktopLyricViewModel>();
            services.AddSingleton(new MusicCacheService(64, Path.Combine(dir, "cache"), new HttpClient()));
        });
        var player = ServiceLocator.Get<PlayerViewModel>();
        var lyric = ServiceLocator.Get<LyricViewModel>();
        using var model = ServiceLocator.Get<DesktopLyricViewModel>();
        var path = Path.Combine(dir, "test.mp3");
        File.WriteAllText(Path.ChangeExtension(path, ".lrc"), "[offset:500]\n[00:01.00]第一句\n[00:03.00]第二句");
        var song = new Song { Name = "本地测试", Artist = "歌手", Source = MusicSource.Local, LocalFilePath = path };
        player.CurrentSong = song;
        var load = lyric.LoadAsync(song);
        lyric.UpdatePosition(2800);
        PumpUntil(() => load.IsCompleted && lyric.HasLyric);
        Check(model.Original == "第二句", "加载完成立即定位进度与 offset");
        lyric.UpdatePosition(600);
        Check(model.Original == "第一句", "向后跳转同步");
        lyric.Lines[0].Translation = "Translation";
        model.ShowTranslation = false;
        model.ShowTranslation = true;
        Check(model.Translation == "Translation", "翻译显示");
        model.ShowTranslation = false;
        Check(!model.HasTranslation, "隐藏翻译");
        player.CurrentSong = new Song { Name = "新歌曲" };
        Check(model.Original == "新歌曲", "切歌立即清除旧词");
        model.Enabled = true;
        model.FontSize = 40;
        model.BackgroundOpacity = 0.25;
        model.ClickThrough = true;
        state.Flush();
        var restored = new AppStateStore(Path.Combine(dir, "state.json"));
        Check(restored.DesktopLyricEnabled && restored.DesktopLyricFontSize == 40
            && restored.DesktopLyricBackgroundOpacity == 0.25 && restored.DesktopLyricClickThrough,
            "偏好持久化（含穿透）");

        // 暂停时隐藏歌词:暂停/停止收起,继续播放恢复;换歌的 Loading 不能算暂停(否则每切一首闪一下)。
        model.HideWhenPaused = true;
        Check(!model.IsWindowVisible, "暂停时隐藏歌词窗口");
        player.IsPlaying = true;
        Check(model.IsWindowVisible, "继续播放后歌词重新显示");
        player.IsPlaying = false;
        player.IsLoading = true;
        Check(model.IsWindowVisible, "换歌加载中不闪");
        player.IsLoading = false;
        Check(!model.IsWindowVisible, "加载结束仍暂停时重新收起");
        state.Flush();
        Check(new AppStateStore(Path.Combine(dir, "state.json")).DesktopLyricHideWhenPaused,
            "暂停隐藏开关落盘");
        model.HideWhenPaused = false;
        Check(model.IsWindowVisible, "关掉开关立即恢复显示");

        // ---- 控制器层:暂停时真的收起窗口,继续播放原地回来(位置不重算) ----
        // 放在窗口交互断言之前:后面那条"穿透收起工具栏"是历史遗留的红灯,会中断后面的所有断言。
        model.Enabled = false;
        model.ClickThrough = false;
        using (var controller = new DesktopLyricController(model, state))
        {
            model.Enabled = true;
            Dispatcher.UIThread.RunJobs();
            var lyricWindow = controller.LyricWindow ?? throw new InvalidOperationException("未创建歌词窗口");
            Check(lyricWindow.IsVisible, "开启歌词后窗口显示");
            var resting = lyricWindow.Position;
            model.HideWhenPaused = true;
            Dispatcher.UIThread.RunJobs();
            Check(!lyricWindow.IsVisible, "暂停时窗口真的收起（不只是透明）");
            player.IsPlaying = true;
            Dispatcher.UIThread.RunJobs();
            Check(lyricWindow.IsVisible && lyricWindow.Position == resting, "继续播放后原地恢复");
            player.IsPlaying = false;
            Dispatcher.UIThread.RunJobs();
            Check(!lyricWindow.IsVisible, "再次暂停重新收起");
        }
        Check(model.Enabled, "收起窗口不改写歌词总开关");
        // 还原状态给后续断言（「关闭歌词解除穿透」需要先处于穿透状态）。
        model.HideWhenPaused = false;
        model.Enabled = true;
        model.ClickThrough = true;

        // 关闭歌词要连带收回穿透,且不能把 true 残留在配置里(否则下次打开一上来就点不动)。
        model.Enabled = false;
        Check(!model.ClickThrough, "关闭歌词解除穿透");
        Check(!new AppStateStore(Path.Combine(dir, "state.json")).DesktopLyricClickThrough,
            "穿透复位已落盘");
        model.Enabled = true; // 恢复,后面的窗口交互测试需要工具栏可点
        var window = new DesktopLyricWindow { DataContext = model };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Check(window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "新歌曲"), "窗口编译绑定");
        var dragArea = window.FindControl<Border>("ToolbarDragArea")!;
        var dragPoint = dragArea.TranslatePoint(new Avalonia.Point(dragArea.Bounds.Width / 2, dragArea.Bounds.Height / 2), window)!.Value;
        Check(dragArea.Bounds.Width > 0 && ReferenceEquals(window.InputHitTest(dragPoint), dragArea), "工具栏左侧空白可接收拖动输入");
        var translationButton = window.FindControl<ToggleButton>("TranslationButton")!;
        var buttonPoint = translationButton.TranslatePoint(new Avalonia.Point(translationButton.Bounds.Width / 2, translationButton.Bounds.Height / 2), window)!.Value;
        window.MouseDown(buttonPoint, MouseButton.Left);
        window.MouseUp(buttonPoint, MouseButton.Left);
        Check(model.ShowTranslation, "工具栏翻译按钮点击生效且未被拖动区域拦截");
        model.ShowTranslation = false;
        Check(translationButton.IsChecked == false, "翻译按钮跟随设置同步");
        var content = (Control)window.Content!;
        using (var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(
            new Avalonia.PixelSize((int)Math.Ceiling(content.Bounds.Width), (int)Math.Ceiling(content.Bounds.Height))))
        {
            bitmap.Render(content);
            bitmap.Save(Path.Combine(Path.GetTempPath(), "aly-desktop-lyrics-preview.png"), PngBitmapEncoderOptions.Default);
        }
        var lyricText = window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "新歌曲");
        var lyricOrigin = lyricText.PointToScreen(default);
        var expandedPosition = window.ExpandedPosition;
        var windowSize = window.ClientSize;
        model.ClickThrough = true;
        Dispatcher.UIThread.RunJobs();
        Check(lyricText.PointToScreen(default) == lyricOrigin && window.ClientSize.Height < windowSize.Height
            && window.ExpandedPosition == expandedPosition,
            "开启穿透收起工具栏，歌词屏幕位置与保存位置不变");
        model.ClickThrough = false;
        Dispatcher.UIThread.RunJobs();
        Check(lyricText.PointToScreen(default) == lyricOrigin && window.ClientSize == windowSize
            && window.ExpandedPosition == expandedPosition,
            "解除穿透恢复窗口，歌词屏幕位置与保存位置不变");
        for (var i = 0; i < 8; i++)
        {
            model.ClickThrough = true;
            Dispatcher.UIThread.RunJobs();
            model.ClickThrough = false;
            Dispatcher.UIThread.RunJobs();
        }
        Check(lyricText.PointToScreen(default) == lyricOrigin && window.ExpandedPosition == expandedPosition,
            "反复切换穿透不累积位移");
        model.ClickThrough = true;
        model.CloseCommand.Execute(null);
        Check(!model.Enabled && !model.ClickThrough, "关闭同时解除穿透");
        Check(!new AppStateStore(Path.Combine(dir, "state.json")).DesktopLyricClickThrough,
            "关闭歌词后穿透不残留在配置里");
        window.Close();
        var main = ServiceLocator.Get<MainViewModel>();
        main.Settings.OpenDesktopLyricDialogCommand.Execute(null);
        Check(main.IsDesktopLyricDialogOpen && main.IsAnyModalOpen, "歌词关闭时仍可打开设置模态框");
        var dialog = new DesktopLyricSettingsDialogView { DataContext = main };
        var dialogWindow = new Window { Width = 360, Height = 560, Content = dialog };
        dialogWindow.Show();
        Dispatcher.UIThread.RunJobs();
        var sliders = dialog.GetVisualDescendants().OfType<Slider>().ToArray();
        Check(sliders.Length == 2 && sliders.All(s => s.Bounds.Width > 0 && s.Bounds.Width < 360), "窄屏滑块布局");
        sliders[0].Value = 32;
        Check(model.FontSize == 32, "弹窗调整即时同步");
        model.ClickThrough = true;
        Check(main.TryHandleBack() && !main.IsDesktopLyricDialogOpen && !main.IsAnyModalOpen, "返回键关闭模态框");
        main.Settings.OpenDesktopLyricDialogCommand.Execute(null);
        Check(main.IsDesktopLyricDialogOpen, "穿透时仍可重新打开设置");
        using (var preview = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(360, 560)))
        {
            preview.Render(dialog);
            preview.Save(Path.Combine(Path.GetTempPath(), "aly-lyric-dialog-preview.png"), PngBitmapEncoderOptions.Default);
        }
        dialog.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Escape });
        Check(!main.IsDesktopLyricDialogOpen, "Esc 关闭模态框");
        dialogWindow.Close();
        Console.WriteLine("PASS: desktop lyrics");
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Dispatcher.UIThread.RunJobs();
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            Thread.Sleep(10);
        }
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: " + message);
    }
}
