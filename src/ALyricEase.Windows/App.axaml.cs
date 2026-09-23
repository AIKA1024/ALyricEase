using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ALyricEase.Infrastructure;
using ALyricEase.Services;
using ALyricEase.Services.Smtc;
using ALyricEase.Services.Taskbar;
using ALyricEase.ViewModels;

namespace ALyricEase;

public partial class App : Application
{
  public override void Initialize()
  {
    AvaloniaXamlLoader.Load(this);

#if DEBUG
    // Avalonia 12 Developer Tools:按 F12 连接独立的 avdt 调试进程
    this.AttachDeveloperTools();
#endif
  }

  public override void OnFrameworkInitializationCompleted()
  {
    if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
    {
      // 启动画面先亮(极轻量),主窗口的重量级构建延后到它渲染出首帧之后;
      // 关闭语义随之改为主窗口关闭 —— 否则启动画面先关会把应用带崩。
      desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
      desktop.Exit += (_, _) => ServiceLocator.Get<AppStateStore>().Flush();

      var splash = new SplashWindow();
      splash.Show();

      _ = ShowMainWindowAsync(desktop, splash);
    }

    base.OnFrameworkInitializationCompleted();
  }

  /// <summary>等启动画面渲染出首帧后构建/显示主窗口,再收启动画面。
  /// 构建失败时关掉启动画面并显式关机,避免无窗口挂着退出不去。</summary>
  private async Task ShowMainWindowAsync(
      IClassicDesktopStyleApplicationLifetime desktop, SplashWindow splash)
  {
    try
    {
      await splash.WaitForReadyAsync();

      var mainWindow = new MainWindow
      {
        DataContext = ServiceLocator.Get<MainViewModel>(),
      };
      // 在显示前恢复几何,确保 Win32 窗口的第一个可见帧就是上次关闭时的大小、位置和状态。
      mainWindow.RestorePersistedWindowBounds();
      desktop.MainWindow = mainWindow;
      mainWindow.Show();

#if WINDOWS
      // SMTC 需要前台窗口 HWND,须在窗口创建后于 UI 线程初始化
      var hwnd = mainWindow.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
      ServiceLocator.Get<ISmtcService>().Initialize(hwnd);

      // 任务栏缩略图工具栏:须窗口已显示(关联任务栏按钮)后再注册 → 挂 Opened
      mainWindow.Opened += (_, _) => InitTaskbarThumbButtons(mainWindow, hwnd);
#endif

      // 后台恢复登录态(已存 MUSIC_U 则拉资料+歌单),不阻塞 UI
      _ = RestoreLoginAsync();

      // 主窗口渲染出首帧后再收启动画面,衔接不留白
      if (TopLevel.GetTopLevel(mainWindow) is { } topLevel)
      {
        var firstFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        topLevel.RequestAnimationFrame(_ => firstFrame.TrySetResult());
        await firstFrame.Task;
      }
    }
    finally
    {
      splash.Close();
    }
  }

#if WINDOWS
  private void InitTaskbarThumbButtons(Avalonia.Controls.Window window, IntPtr hwnd)
  {
    try
    {
      var tb = new TaskbarThumbButtons(hwnd);
      if (!tb.Initialize())
      {
        tb.Dispose();
        return;
      }

      var player = ServiceLocator.Get<PlayerViewModel>();
      // 播放状态变化 → 更新播放/暂停按钮图标
      player.PropertyChanged += (_, e) =>
      {
        if (e.PropertyName == nameof(PlayerViewModel.IsPlaying))
          tb.SetPlaying(player.IsPlaying);
      };
      tb.ButtonClicked += id =>
      {
        switch (id)
        {
          case TaskbarThumbButtons.IdPlayPause:
            player.TogglePlayPauseCommand.Execute(null);
            break;
          case TaskbarThumbButtons.IdNext:
            _ = player.PlayNextAsync();
            break;
          case TaskbarThumbButtons.IdPrevious:
            _ = player.PlayPreviousAsync();
            break;
        }
      };
    }
    catch
    {
      // 缩略图工具栏失败 → 静默降级
    }
  }
#endif

  private static async Task RestoreLoginAsync()
  {
    try
    {
      await ServiceLocator.Get<PlaylistViewModel>().EnsureLoadedAsync();
    }
    catch
    {
      // 恢复登录失败不阻塞启动
    }
  }
}
