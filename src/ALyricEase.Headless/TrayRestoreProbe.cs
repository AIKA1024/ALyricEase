using System.Diagnostics;
using System.Runtime.InteropServices;
using ALyricEase.Services;
using ALyricEase.Services.Audio;
using ALyricEase.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace ALyricEase.Headless;

/// <summary>真实 Windows 托盘恢复回归：检查前台窗口、原生最小化状态及窗口几何。</summary>
internal static class TrayRestoreProbe
{
    public static int Run(string outputPath)
    {
        using var output = new StreamWriter(outputPath) { AutoFlush = true };
        var exitCode = 0;
        Dispatcher.UIThread.Post(async () =>
        {
            try { await RunAsync(output); }
            catch (Exception ex) { output.WriteLine($"[tray-restore] FAIL: {ex}"); exitCode = 1; }
            finally { (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown(); }
        });
        AppBuilder.Configure<ProbeApp>().UsePlatformDetect()
            .StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown);
        return exitCode;
    }

    private static async Task RunAsync(TextWriter output)
    {
        var root = Path.Combine(Path.GetTempPath(), "ALyricEase.TrayRestoreProbe", Guid.NewGuid().ToString("N"));
        var state = new AppStateStore(Path.Combine(root, "state.json")) { MinimizeToTrayOnClose = true };
        using var http = new HttpClient();
        using var player = new StubPlayer();
        var settings = new SettingsViewModel(state, new MusicCacheService(128, Path.Combine(root, "cache"), http), player);
        var host = new ContentControl { IsVisible = false };
        var main = new Window
        {
            Title = "ALyricEase 托盘恢复回归", Width = 640, Height = 560,
            Position = new PixelPoint(160, 120), WindowStartupLocation = WindowStartupLocation.Manual,
            WindowDecorations = WindowDecorations.BorderOnly,
            ExtendClientAreaToDecorationsHint = true,
            Content = new Grid { Children = { new TextBlock { Text = "主窗口：恢复后应直接在前台" }, host } },
        };
        var other = new Window
        {
            Title = "模拟托盘/菜单收起后恢复前台的窗口", Width = 750, Height = 650,
            Position = new PixelPoint(100, 80), WindowStartupLocation = WindowStartupLocation.Manual,
        };
        using var controller = new DesktopLifecycleController(Application.Current!, main, settings, state, host);
        var tray = TrayIcon.GetIcons(Application.Current!)![0];
        try
        {
            main.Show();
            other.Show();
            await Task.Delay(100);
            // 后台启动的探针先用一次真实输入建立前台测试条件，后续恢复不再注入输入。
            other.Topmost = true;
            GetCursorPos(out var cursor);
            var point = other.PointToScreen(new Point(100, 100));
            SetCursorPos(point.X, point.Y);
            MouseEvent(0x0002 /* LEFTDOWN */, 0, 0, 0, UIntPtr.Zero);
            MouseEvent(0x0004 /* LEFTUP */, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(60);
            SetCursorPos(cursor.X, cursor.Y);
            other.Topmost = false;
            var mainHwnd = main.TryGetPlatformHandle()!.Handle;
            var otherHwnd = other.TryGetPlatformHandle()!.Handle;
            Check(GetForegroundWindow() == otherHwnd, "测试遮挡窗口已处于前台", output);

            foreach (var original in new[] { WindowState.Normal, WindowState.Maximized })
            {
                main.WindowState = original;
                main.Activate();
                await Task.Delay(80);
                main.Close();
                await WaitUntilAsync(() => !main.IsVisible);
                other.Activate();

                // 模拟托盘消息/菜单命令返回时 Shell 再把前台交还给原窗口。
                // 旧的同步 Show+Activate 在这里失效；投递的激活应在此后执行。
                tray.Command!.Execute(null);
                other.Activate();
                await WaitUntilAsync(() => GetForegroundWindow() == mainHwnd);
                Check(main.IsVisible && IsWindowVisible(mainHwnd) && !IsIconic(mainHwnd),
                    $"{original} 态从托盘恢复后实际可见且未最小化", output);
                Check(main.WindowState == original, $"恢复保留 {original} 窗口状态", output);
                Check(!main.Topmost, "恢复后保持普通窗口，不永久置顶", output);
            }

            // 从正常/最大化状态最小化，再隐藏到托盘，恢复仍应展开并保留此前状态。
            foreach (var original in new[] { WindowState.Normal, WindowState.Maximized })
            {
                main.WindowState = original;
                main.Close();
                await WaitUntilAsync(() => !main.IsVisible);
                controller.RestoreWindow();
                await WaitUntilAsync(() => GetForegroundWindow() == mainHwnd);
                main.WindowState = WindowState.Minimized;
                await Task.Delay(80);
                main.Close();
                await WaitUntilAsync(() => !main.IsVisible);
                tray.Command!.Execute(null);
                await WaitUntilAsync(() => GetForegroundWindow() == mainHwnd && !IsIconic(mainHwnd));
                Check(main.WindowState == original, $"{original}→最小化→托盘后恢复此前状态", output);
            }

            main.WindowState = WindowState.Normal;
            await Task.Delay(80);
            var position = main.Position;
            var size = main.ClientSize;
            tray.Command!.Execute(null);
            await Task.Delay(80);
            Check(main.Position == position && main.ClientSize == size, "重复点击托盘不改变窗口位置和大小", output);

            main.Close();
            await WaitUntilAsync(() => !main.IsVisible);
            controller.RestoreWindow();
            controller.Exit();
            await Task.Delay(80);
            Check(!IsWindowVisible(mainHwnd), "退出后取消已排队的激活，不重新显示窗口", output);
            output.WriteLine("[tray-restore] PASS");
        }
        finally
        {
            controller.Exit();
            other.Close();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(2)) throw new TimeoutException("窗口未恢复为可见的前台窗口");
            await Task.Delay(20);
        }
    }

    private static void Check(bool condition, string message, TextWriter output)
    {
        if (!condition) throw new InvalidOperationException(message);
        output.WriteLine($"[tray-restore] OK: {message}");
    }

    public sealed class ProbeApp : Application
    {
        public override void Initialize()
        {
            AssetLoader.SetDefaultAssembly(typeof(PlayerViewModel).Assembly);
            Styles.Add(new FluentTheme());
        }
    }

    private sealed class StubPlayer : IAudioPlayer
    {
        public PlaybackState State => PlaybackState.Idle;
        public long PositionMs { get; set; }
        public long DurationMs => 180000;
        public int Volume { get; set; }
        public event EventHandler? StateChanged { add { } remove { } }
        public event EventHandler<long>? PositionChanged { add { } remove { } }
        public event EventHandler<long>? DurationChanged { add { } remove { } }
        public event EventHandler<string>? ErrorOccurred { add { } remove { } }
        public void PlayUrl(string url) { }
        public void Pause() { }
        public void Resume() { }
        public void Stop() { }
        public void Dispose() { }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr hwnd);
    [StructLayout(LayoutKind.Sequential)] private struct CursorPoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out CursorPoint point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", EntryPoint = "mouse_event")] private static extern void MouseEvent(uint flags, uint x, uint y, uint data, UIntPtr extraInfo);
}
