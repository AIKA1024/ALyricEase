using Avalonia;
using ALyricEase.Infrastructure;
using ALyricEase.Services.Audio;
using ALyricEase.Services.Auth;
using ALyricEase.Services.Crypto;
using ALyricEase.Services.NetEase;
using ALyricEase.Services.Smtc;
using ALyricEase.ViewModels;
using LibVLCSharp.Shared;
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
    // LibVLC 原生库必须最先加载
    Core.Initialize();

    // selftest(无 UI)模式下:先在主线程钉住 UIThread,避免被 libvlc 后台线程抢绑
    _ = Avalonia.Threading.Dispatcher.UIThread;

    if (args.Length > 0 && args[0] == "--selftest-leak")
    {
      SelfTest.RunLeakTestAsync().GetAwaiter().GetResult();
      return;
    }

    if (args.Length > 0 && args[0] == "--selftest-play")
    {
      SelfTest.RunPlayAsync().GetAwaiter().GetResult();
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
    services.AddSingleton<IAudioPlayer, LibVlcAudioPlayer>();
    services.AddSingleton<SmtcService>();
    services.AddSingleton<LyricViewModel>();
    services.AddSingleton<PlayerViewModel>();
    services.AddSingleton<SearchViewModel>();
    services.AddSingleton<PlaylistViewModel>();
    services.AddSingleton<MainViewModel>();
    ServiceLocator.Provider = services.BuildServiceProvider();

    return AppBuilder.Configure<App>()
      .UsePlatformDetect()
      .WithInterFont()
      .LogToTrace();
  }
}
