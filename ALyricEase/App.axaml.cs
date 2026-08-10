using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ALyricEase.Infrastructure;
using ALyricEase.Services.Smtc;
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
      desktop.MainWindow = new MainWindow
      {
        DataContext = ServiceLocator.Get<MainViewModel>(),
      };

      // SMTC 需要前台窗口 HWND,须在窗口创建后于 UI 线程初始化
      var hwnd = desktop.MainWindow.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
      ServiceLocator.Get<SmtcService>().Initialize(hwnd);

      // 后台恢复登录态(已存 MUSIC_U 则拉资料+歌单),不阻塞 UI
      _ = RestoreLoginAsync();
    }

    base.OnFrameworkInitializationCompleted();
  }

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
