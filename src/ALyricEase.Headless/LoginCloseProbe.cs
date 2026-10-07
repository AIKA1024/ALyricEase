using System.Diagnostics;
using System.Reflection;
using ALyricEase.Infrastructure;
using ALyricEase.Services.NetEase;
using ALyricEase.ViewModels;
using ALyricEase.Views;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Threading;

namespace ALyricEase.Headless;

/// <summary>用阻塞的原生关闭模拟慢代理退出，验证 UI 淡出及快速重开不互相干扰。</summary>
internal static class LoginCloseProbe
{
    public static async Task RunAsync()
    {
        if (!NetEaseProxyLoginService.IsSupported) return;
        var root = Path.Combine(Path.GetTempPath(), $"aly-login-close-{Guid.NewGuid():N}");
        var main = QqUserFailureProbe.CreateOfflineMain(root);
        main.OpenLoginDialogCommand.Execute(null);
        var playlist = main.Playlist;
        using var releaseTermination = new ManualResetEventSlim();
        var terminationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminatedOnUiThread = true;
        var service = new NetEaseProxyLoginService(ServiceLocator.Get<NetEaseApiClient>(),
            _ => Task.FromResult("probe"), proxy =>
            {
                terminatedOnUiThread = Dispatcher.UIThread.CheckAccess();
                terminationStarted.TrySetResult();
                releaseTermination.Wait(TimeSpan.FromSeconds(5));
                proxy.Terminate();
            });
        service.Start(_ => { });
        typeof(PlaylistViewModel).GetField("_netEaseProxyLogin", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(playlist, service);
        playlist.IsNetEaseProxyLoginActive = true;

        var view = new LoginDialogView { DataContext = main, IsVisible = false };
        view.Bind(ModalVisibilityTransition.IsOpenProperty, new Binding(nameof(MainViewModel.IsLoginDialogOpen)));
        var window = new Window { Width = 1000, Height = 720, Content = view };
        window.Show();
        await ModalVisibilityTransition.WaitForTransitionAsync(view);
        try
        {
            var watch = Stopwatch.StartNew();
            main.CloseLoginDialogCommand.Execute(null);
            watch.Stop();
            Check(watch.ElapsedMilliseconds < 100 && !main.IsLoginDialogOpen,
                $"关闭命令及时返回({watch.ElapsedMilliseconds}ms)");
            await terminationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Check(!terminatedOnUiThread, "原生 Terminate 在后台执行");
            Check(view.IsVisible && !view.IsEnabled, "关闭时先禁用输入，保持可见直到淡出结束");

            var animation = ModalVisibilityTransition.WaitForTransitionAsync(view);
            var sawIntermediateOpacity = false;
            while (!animation.IsCompleted)
            {
                await Task.Delay(10);
                sawIntermediateOpacity |= view.Opacity is > 0 and < 1;
            }
            await animation;
            Check(sawIntermediateOpacity && !view.IsVisible, "代理仍在清理时淡出动画正常播放并完成");
            var shutdown = ShutdownTask(playlist);
            Check(!shutdown.IsCompleted, "动画完成不需要等待原生代理关闭");

            var restart = playlist.StartNetEaseProxyLoginCommand.ExecuteAsync(null);
            Check(!restart.IsCompleted, "快速重启代理等待旧实例关闭");
            playlist.CancelNetEaseProxyLogin();
            releaseTermination.Set();
            await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
            await restart;
            Check(!playlist.IsNetEaseProxyLoginActive && playlist.NetEaseProxyPort == 0,
                "等待期间取消不会在清理完成后重新启动");

            await playlist.StartNetEaseProxyLoginCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            Check(playlist.IsNetEaseProxyLoginActive && playlist.NetEaseProxyPort > 0,
                "旧实例关闭后可以重新启动代理");
            playlist.CancelNetEaseProxyLogin();
            await ShutdownTask(playlist).WaitAsync(TimeSpan.FromSeconds(5));
            Dispatcher.UIThread.RunJobs();
            Check(!playlist.IsNetEaseProxyLoginActive, "迟到的进度通知不会恢复已取消的代理状态");
            Console.WriteLine("[login-close] PASS");
        }
        finally
        {
            releaseTermination.Set();
            playlist.CancelLoginActivities();
            await ShutdownTask(playlist).WaitAsync(TimeSpan.FromSeconds(5));
            window.Close();
        }
    }

    private static Task ShutdownTask(PlaylistViewModel playlist)
        => (Task)typeof(PlaylistViewModel).GetField("_netEaseProxyShutdown",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(playlist)!;

    private static void Check(bool passed, string name)
    {
        if (!passed) throw new InvalidOperationException($"[login-close] FAIL {name}");
        Console.WriteLine($"[login-close] PASS {name}");
    }
}
