using System.Diagnostics;
using ALyricEase.Services;
using ALyricEase.ViewModels;
using ALyricEase.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ALyricEase.Infrastructure;

namespace ALyricEase.Headless;

/// <summary>隔离用户配置的关闭、模态、托盘与设置回归；不访问真实系统启动项。</summary>
internal static class DesktopLifecycleProbe
{
    public static int Run()
    {
        var probeBase = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ALyricEase.DesktopLifecycleProbe"));
        var root = Path.GetFullPath(Path.Combine(probeBase, Guid.NewGuid().ToString("N")));
        var statePath = Path.Combine(root, "state.json");
        try
        {
            var state = new AppStateStore(statePath);
            var startup = new StubStartupService();
            using var http = new HttpClient();
            var settings = new SettingsViewModel(state, new MusicCacheService(64, Path.Combine(root, "cache"), http),
                new HeadlessApp.StubAudioPlayer { DeviceSelectionSupported = false }, startupService: startup);
            Check(settings.CloseBehaviorIndex == 0, "新配置每次询问");
            settings.StartAtLogin = true;
            Check(startup.IsEnabled && settings.StartAtLogin, "自启写入后读取系统状态");
            startup.FailWrite = true;
            settings.StartAtLogin = false;
            Check(settings.StartAtLogin && settings.HasStartupHint, "自启设置失败回滚开关并显示提示");
            startup.FailWrite = false;
            startup.Enabled = false;
            settings.RefreshDesktopSettings();
            Check(!settings.StartAtLogin && !settings.HasStartupHint, "重新进入设置刷新外部变更");

            var window = CreateWindow();
            var closed = false;
            window.Closed += (_, _) => closed = true;
            using var controller = new DesktopLifecycleController(Application.Current!, window, settings, state, DialogHost(window));
            window.Show();
            window.Close();
            PumpUntil(() => Dialog(window) is not null);
            Check(!closed && window.IsVisible, "无默认值关闭时主窗口保持打开");
            var dialog = Dialog(window)!;
            Check(ModalVisibilityTransition.GetIsOpen(dialog), "关闭弹层复用统一显隐动画");
            PumpUntil(() => ModalVisibilityTransition.WaitForTransitionAsync(dialog).IsCompleted);
            Check(!((Grid)window.Content!).Children[0].IsEnabled, "遮罩下的主窗口内容禁用");
            for (var i = 0; i < 8; i++)
            {
                window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
                window.KeyRelease(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
                Pump();
                Check(window.FocusManager?.GetFocusedElement() is Control focused && dialog.IsVisualAncestorOf(focused),
                    "Tab 焦点限定在弹层内");
            }
            Capture(window, "close_behavior_dialog.png");
            window.RequestedThemeVariant = ThemeVariant.Dark;
            Capture(window, "close_behavior_dialog_dark.png");
            window.RequestedThemeVariant = ThemeVariant.Light;
            window.Width = 360;
            Capture(window, "close_behavior_dialog_narrow.png");
            window.Width = 640;
            window.Close();
            Pump();
            Check(Dialog(window) == dialog && !closed, "重复关闭只保留一个模态窗口");
            var disabledBeforeHidden = false;
            dialog.PropertyChanged += (_, e) =>
            {
                if (e.Property == Control.IsEnabledProperty && !dialog.IsEnabled)
                    disabledBeforeHidden = dialog.IsVisible;
            };
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            PumpUntil(() => Dialog(window) is null);
            Check(disabledBeforeHidden, "Esc 取消时先淡出并立即禁用输入");
            Check(window.IsVisible && state.MinimizeToTrayOnClose is null, "取消不关闭也不保存偏好");
            Check(((Grid)window.Content!).Children[0].IsEnabled, "淡出后恢复背景交互");

            window.Close();
            PumpUntil(() => Dialog(window) is not null);
            var minimizeDialog = Dialog(window)!;
            var fadeFinishedAtHide = false;
            void OnHidden(object? sender, AvaloniaPropertyChangedEventArgs e)
            {
                if (e.Property == Window.IsVisibleProperty && !window.IsVisible)
                    fadeFinishedAtHide = !minimizeDialog.IsVisible &&
                        ModalVisibilityTransition.WaitForTransitionAsync(minimizeDialog).IsCompleted;
            }
            window.PropertyChanged += OnHidden;
            minimizeDialog.ViewModel.MinimizeToTrayCommand.Execute(null);
            PumpUntil(() => !window.IsVisible);
            window.PropertyChanged -= OnHidden;
            Check(fadeFinishedAtHide, "最小化到托盘前保留完整淡出动画");
            Check(!closed && state.MinimizeToTrayOnClose is null, "未勾记住时仅本次隐藏到托盘");
            controller.RestoreWindow();
            Check(window.IsVisible && !closed, "托盘恢复同一主窗口");

            window.Close();
            PumpUntil(() => Dialog(window) is not null);
            dialog = Dialog(window)!;
            dialog.ViewModel.RememberChoice = true;
            dialog.ViewModel.MinimizeToTrayCommand.Execute(null);
            PumpUntil(() => !window.IsVisible);
            Check(new AppStateStore(statePath).MinimizeToTrayOnClose == true && settings.CloseBehaviorIndex == 1,
                "记住托盘选择落盘并同步设置");
            controller.RestoreWindow();
            window.Close();
            PumpUntil(() => !window.IsVisible);
            Check(Dialog(window) is null && !closed, "保存托盘行为后不再询问");
            controller.Exit();
            Check(closed, "托盘退出绕过已保存的最小化偏好");

            settings.CloseBehaviorIndex = 0;
            Check(new AppStateStore(statePath).MinimizeToTrayOnClose is null, "设置可重置为每次询问");
            window = CreateWindow();
            closed = false;
            window.Closed += (_, _) => closed = true;
            using (var exitController = new DesktopLifecycleController(Application.Current!, window, settings, state, DialogHost(window)))
            {
                window.Show();
                window.Close();
                PumpUntil(() => Dialog(window) is not null);
                dialog = Dialog(window)!;
                dialog.ViewModel.RememberChoice = true;
                dialog.ViewModel.ExitCommand.Execute(null);
                PumpUntil(() => closed);
            }
            Check(new AppStateStore(statePath).MinimizeToTrayOnClose == false && settings.CloseBehaviorIndex == 2,
                "记住退出选择后真正关闭并落盘");

            window = CreateWindow();
            closed = false;
            window.Closed += (_, _) => closed = true;
            using (var directController = new DesktopLifecycleController(Application.Current!, window, settings, state, DialogHost(window)))
            {
                window.Show();
                window.Close();
                Check(closed && Dialog(window) is null, "保存退出行为后直接关闭");
            }

            settings.CloseBehaviorIndex = 0;
            window = CreateWindow();
            closed = false;
            window.Closed += (_, _) => closed = true;
            using (var oneTimeController = new DesktopLifecycleController(Application.Current!, window, settings, state, DialogHost(window)))
            {
                window.Show();
                window.Close();
                PumpUntil(() => Dialog(window) is not null);
                Dialog(window)!.ViewModel.ExitCommand.Execute(null);
                PumpUntil(() => closed);
                Check(new AppStateStore(statePath).MinimizeToTrayOnClose is null, "未勾记住退出时不写默认值");
            }
            Console.WriteLine("[desktop-lifecycle] PASS");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[desktop-lifecycle] FAIL: {ex}");
            return 1;
        }
        finally
        {
            if (root.StartsWith(probeBase + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static Window CreateWindow() => new()
    {
        Width = 640,
        Height = 560,
        Content = new Grid
        {
            Children =
            {
                new Border
                {
                    Background = Brushes.LightGray,
                    Padding = new Thickness(24),
                    Child = new StackPanel
                    {
                        Spacing = 16,
                        Children =
                        {
                            new TextBlock { Text = "ALyricEase", FontSize = 24 },
                            new TextBlock { Text = "正在播放 · 主窗口内容" },
                            new Button { Content = "背景操作" },
                        },
                    },
                },
                new ContentControl { IsVisible = false, Focusable = false },
            },
        },
    };

    private static ContentControl DialogHost(Window window) =>
        ((Grid)window.Content!).Children.OfType<ContentControl>().Single();

    private static CloseBehaviorDialogView? Dialog(Window window) => DialogHost(window).Content as CloseBehaviorDialogView;

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        Console.WriteLine($"[desktop-lifecycle] OK: {name}");
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var timeout = Stopwatch.StartNew();
        do
        {
            Pump();
            if (condition()) return;
            Thread.Sleep(5);
        } while (timeout.Elapsed < TimeSpan.FromSeconds(3));
        throw new TimeoutException("等待窗口状态更新超时");
    }

    private static void Capture(Window window, string fileName)
    {
        Pump();
        using var bitmap = new RenderTargetBitmap(
            new PixelSize((int)window.ClientSize.Width, (int)window.ClientSize.Height), new Vector(96, 96));
        bitmap.Render(window);
        var directory = Path.Combine(Directory.GetCurrentDirectory(), "tmpandroid");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        bitmap.Save(path, PngBitmapEncoderOptions.Default);
        Console.WriteLine($"[desktop-lifecycle] 截图: {path}");
    }

    private sealed class StubStartupService : IStartupService
    {
        public bool IsSupported => true;
        public bool Enabled { get; set; }
        public bool IsEnabled => Enabled;
        public bool FailWrite { get; set; }
        public void SetEnabled(bool enabled)
        {
            if (FailWrite) throw new IOException("probe failure");
            Enabled = enabled;
        }
    }
}
