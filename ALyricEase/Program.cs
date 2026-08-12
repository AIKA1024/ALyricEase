using System.Threading;
using Avalonia;
using Avalonia.Threading;
using ALyricEase.Infrastructure;
using ALyricEase.Services.Audio;
using ALyricEase.Services.Auth;
using ALyricEase.Services.Crypto;
using ALyricEase.Services.NetEase;
using ALyricEase.Services.Smtc;
using ALyricEase.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace ALyricEase;

class Program
{
  // Initialization code. Don't use any Avalonia, third-party APIs or any
  // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
  // yet and stuff might break.
  [STAThread]
  public static void Main(string[] args)
  {
    // selftest(无 UI)模式下:先在主线程钉住 UIThread,保证 DispatcherService.Post 能排队
    _ = Avalonia.Threading.Dispatcher.UIThread;

    if (args.Length > 0 && args[0] == "--selftest-leak")
    {
      SelfTest.RunLeakTestAsync().GetAwaiter().GetResult();
      return;
    }

    if (args.Length > 0 && args[0] == "--selftest-play")
    {
      var task = SelfTest.RunPlayAsync();
      // 播放事件经 DispatcherService.Post 排入 UIThread 队列;selftest 体跑在线程池,
      // 只能由属主线程(主线程)泵 RunJobs——否则 DispatcherOperation.Execute 里的
      // AvaloniaSynchronizationContext.Ensure 线程校验会抛 InvalidOperationException。
      while (!task.IsCompleted)
      {
        Dispatcher.UIThread.RunJobs();
        Thread.Sleep(5);
      }
      task.GetAwaiter().GetResult();
      return;
    }

    if (args.Length > 0 && args[0] == "--selftest")
    {
      SelfTest.RunAsync().GetAwaiter().GetResult();
      return;
    }

    BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
  }

  // Avalonia configuration, don't remove; also used by visual designer.
  public static AppBuilder BuildAvaloniaApp()
  {
    var services = new ServiceCollection();
    services.AddSingleton<DispatcherService>();
    services.AddSingleton<CookieStore>();
    services.AddSingleton<CnIpPool>();
    services.AddSingleton<CryptoService>();
    services.AddSingleton<NetEaseApiClient>();
#if ANDROID
    services.AddSingleton<IAudioPlayer, AndroidMediaPlayer>();
    services.AddSingleton<ISmtcService, SmtcServiceStub>();
#else
    services.AddSingleton<IAudioPlayer, WindowsMediaPlayer>();
    services.AddSingleton<ISmtcService, SmtcService>();
#endif
    services.AddSingleton<LyricViewModel>();
    services.AddSingleton<PlayerViewModel>();
    services.AddSingleton<SearchViewModel>();
    services.AddSingleton<PlaylistViewModel>();
    services.AddSingleton<RecommendViewModel>();
    services.AddSingleton<ArtistViewModel>();
    services.AddSingleton<AlbumViewModel>();
    services.AddSingleton<MainViewModel>();
    ServiceLocator.Provider = services.BuildServiceProvider();

    return AppBuilder.Configure<App>()
      .UsePlatformDetect()
      .WithInterFont()
      .LogToTrace();
  }
}
