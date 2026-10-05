#if WINDOWS
using System.Diagnostics;
using System.Runtime.InteropServices;
using ALyricEase.Services.Taskbar;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace ALyricEase;

/// <summary>真实 Win32/Shell 回归，不访问用户配置、账号或播放器。</summary>
internal static class TaskbarThumbButtonsProbe
{
    public static int Run(string outputPath)
    {
        var exitCode = 0;
        using var output = new StreamWriter(outputPath, append: false) { AutoFlush = true };
        Dispatcher.UIThread.Post(async () =>
        {
            try { await RunAsync(output); }
            catch (Exception ex) { output.WriteLine($"[taskbar] FAIL: {ex}"); exitCode = 1; }
            finally
            {
                (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
            }
        });
        AppBuilder.Configure<ProbeApp>().UsePlatformDetect().WithInterFont()
            .StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown);
        return exitCode;
    }

    private static async Task RunAsync(TextWriter output)
    {
        var window = new Window
        {
            Width = 440, Height = 200, Title = "ALyricEase 任务栏恢复测试",
            Content = new TextBlock
            {
                Text = "验证任务栏三键：首次显示及连续三次隐藏 / 恢复。",
                Margin = new Thickness(24), TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            },
        };
        var hwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        using var buttons = new TaskbarThumbButtons(hwnd);
        var clicks = new List<uint>();
        buttons.ButtonClicked += clicks.Add;
        try
        {
            Check(buttons.Initialize(), "首次显示前初始化窗口消息监听", output);
            Check(buttons.HasRenderedIcons, "复用项目字体生成四个有效媒体图标", output);
            window.Show();
            await WaitUntilAsync(() => buttons.HasRegisteredButtons);
            Check(buttons.TaskbarButtonCreatedCount > 0, "收到真实 Shell 的 TaskbarButtonCreated 并注册三键", output);
            ClickAll(hwnd, clicks, output);

            for (var cycle = 1; cycle <= 3; cycle++)
            {
                var oldCount = buttons.RegistrationCount;
                window.Hide();
                Check(!buttons.HasRegisteredButtons, $"第 {cycle} 次隐藏后清除任务栏注册状态", output);
                buttons.SetPlaying(cycle % 2 != 0);
                await Task.Delay(150);
                window.Show();
                window.Activate();
                await WaitUntilAsync(() => buttons.HasRegisteredButtons && buttons.RegistrationCount > oldCount);
                Check(buttons.TaskbarButtonCreatedCount >= cycle + 1,
                    $"第 {cycle} 次恢复收到真实 Shell 消息并重新注册三键", output);
                ClickAll(hwnd, clicks, output);
            }

            var oldRegistrations = buttons.RegistrationCount;
            SendMessage(hwnd, RegisterWindowMessage("TaskbarButtonCreated"), IntPtr.Zero, IntPtr.Zero);
            Check(buttons.HasRegisteredButtons && buttons.RegistrationCount == oldRegistrations + 1,
                "重复就绪通知可刷新已有工具栏", output);
            buttons.Dispose();
            buttons.Dispose();
            var oldClicks = clicks.Count;
            SendClick(hwnd, TaskbarThumbButtons.IdPlayPause);
            Check(clicks.Count == oldClicks, "释放后没有残留点击订阅，重复释放安全", output);
            output.WriteLine("[taskbar] PASS");
        }
        finally { window.Close(); }
    }

    private static void ClickAll(IntPtr hwnd, List<uint> clicks, TextWriter output)
    {
        var before = clicks.Count;
        SendClick(hwnd, TaskbarThumbButtons.IdPrevious);
        SendClick(hwnd, TaskbarThumbButtons.IdPlayPause);
        SendClick(hwnd, TaskbarThumbButtons.IdNext);
        Check(clicks.Count == before + 3 && clicks.Skip(before).SequenceEqual(new uint[] { 0, 1, 2 }),
            "三键的 WM_COMMAND 各触发一次，顺序和 ID 正确", output);
    }

    private static void SendClick(IntPtr hwnd, uint id) =>
        SendMessage(hwnd, 0x0111, new IntPtr(((long)TaskbarThumbButtons.ThbnClicked << 16) | id), IntPtr.Zero);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(5))
                throw new TimeoutException("未收到任务栏按钮创建或工具栏注册失败");
            await Task.Delay(25);
        }
    }

    private static void Check(bool condition, string message, TextWriter output)
    {
        if (!condition) throw new InvalidOperationException(message);
        output.WriteLine($"[taskbar] OK: {message}");
    }

    public sealed class ProbeApp : Application
    {
        public override void Initialize()
        {
            Avalonia.Platform.AssetLoader.SetDefaultAssembly(typeof(ALyricEase.ViewModels.PlayerViewModel).Assembly);
            Styles.Add(new FluentTheme());
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}
#endif
