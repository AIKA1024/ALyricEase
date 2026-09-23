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
      desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
      desktop.Exit += (_, _) => ServiceLocator.Get<AppStateStore>().Flush();

      // 主窗口本身秒开:内部先显示启动画面(标题栏+图标+合成线程旋转指示),
      // 重内容壳层(MainWindowShell)在其首帧后由 MainWindow 自行挂载并原地收起启动画面。
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

      // 任务栏缩略图工具栏:须窗口已显示(关联任务栏按钮)后再注册。
      // ⚠ 不能挂 Opened —— Opened 在 Show() 内部同步触发,这里的处理器永远不跑
      // (实测任务栏三键消失的回归);Show() 返回后窗口已显示,前置条件已满足,直接调用。
      InitTaskbarThumbButtons(mainWindow, hwnd);
#endif

      // 后台恢复登录态(已存 MUSIC_U 则拉资料+歌单),不阻塞 UI
      _ = RestoreLoginAsync();
    }

    base.OnFrameworkInitializationCompleted();
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
