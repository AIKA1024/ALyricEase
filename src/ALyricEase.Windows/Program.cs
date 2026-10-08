using System.Threading;
using Avalonia;
using Avalonia.Threading;
using ALyricEase.Infrastructure;
using ALyricEase.Services;
using ALyricEase.Services.Audio;
using ALyricEase.Services.Auth;
using ALyricEase.Services.Crypto;
using ALyricEase.Services.NetEase;
using ALyricEase.Services.QQMusic;
using ALyricEase.Services.Sharing;
using ALyricEase.Services.Smtc;
using ALyricEase.Services.Update;
using ALyricEase.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Velopack;

namespace ALyricEase;

class Program
{
  // Initialization code. Don't use any Avalonia, third-party APIs or any
  // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
  // yet and stuff might break.
  [STAThread]
  public static void Main(string[] args)
  {
    // Velopack 启动钩子:处理 Update.exe 的安装/更新/卸载回调参数(--squirrel-*),
    // 必须在任何其它逻辑前调用;未安装(开发目录直跑)时只记警告不阻断。
    VelopackApp.Build().Run();
    // selftest(无 UI)模式下:先在主线程钉住 UIThread,保证 DispatcherService.Post 能排队
    _ = Avalonia.Threading.Dispatcher.UIThread;

    if (args.Length > 0 && args[0] == "--selftest-taskbar")
    {
      var outputPath = args.Length > 1 ? args[1] : System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ale_taskbar_probe.log");
      Environment.ExitCode = TaskbarThumbButtonsProbe.Run(outputPath);
      return;
    }

    if (args.Length > 0 && args[0] == "--selftest-leak")
    {
      SelfTest.RunLeakTestAsync().GetAwaiter().GetResult();
      return;
    }

    if (args.Length > 0 && args[0] == "--selftest-smtc")
    {
      var task = SelfTest.RunSmtcProbeAsync();
      while (!task.IsCompleted)
      {
        Dispatcher.UIThread.RunJobs();
        Thread.Sleep(5);
      }

      task.GetAwaiter().GetResult();
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

    if (args.Length > 0 && args[0] == "--selftest-audiodev")
    {
      // WinRT 枚举完成回调/Posted 动作需要属主线程泵队列,与 --selftest-play 同款等待方式
      var task = SelfTest.RunAudioDeviceTestAsync();
      while (!task.IsCompleted)
      {
        Dispatcher.UIThread.RunJobs();
        Thread.Sleep(5);
      }

      task.GetAwaiter().GetResult();
      return;
    }

    if (args.Length > 0 && args[0] == "--selftest-xfade")
    {
      // 交叉淡化真机自测:生成两段 WAV,验证双 WinRT MediaPlayer 同时发声与切换语义
      var xfadeTask = SelfTest.RunXfadeTestAsync();
      while (!xfadeTask.IsCompleted)
      {
        Dispatcher.UIThread.RunJobs();
        Thread.Sleep(5);
      }

      xfadeTask.GetAwaiter().GetResult();
      return;
    }

    if (args.Length > 0 && args[0] == "--selftest-audioenergy")
    {
      // 真机验证播放会话峰值能被背景分析器读到；同样需要泵 UI 队列更新 Playing 状态。
      var energyTask = SelfTest.RunAudioEnergyTestAsync();
      while (!energyTask.IsCompleted)
      {
        Dispatcher.UIThread.RunJobs();
        Thread.Sleep(5);
      }

      energyTask.GetAwaiter().GetResult();
      return;
    }

    if (args.Length > 0 && args[0] == "--selftest-creator")
    {
      var creatorTask = SelfTest.RunCreatorProbeAsync();
      while (!creatorTask.IsCompleted)
      {
        Dispatcher.UIThread.RunJobs();
        Thread.Sleep(5);
      }

      creatorTask.GetAwaiter().GetResult();
      return;
    }

    if (args.Length > 0 && args[0] == "--selftest")
    {
      SelfTest.RunAsync().GetAwaiter().GetResult();
      return;
    }

    // 分享面板真机探针(--sharetest [direct|flyout|closed]):真窗口 + DataRequested 观测,
    // 定位 WindowsShareService 吞掉的失败(ShowShareUIForWindow 不抛异常 ≠ 面板真的弹出)
    if (args.Length > 0 && args[0] == "--sharetest")
    {
      var shareMode = args.Length > 1 ? args[1] : "direct";
      var shareBuilder = BuildAvaloniaApp();
      Dispatcher.UIThread.Post(async () =>
      {
        try { await ShareProbe.RunAsync(shareMode); }
        finally { Dispatcher.UIThread.InvokeShutdown(); }
      }, DispatcherPriority.Background);
      shareBuilder.StartWithClassicDesktopLifetime(Array.Empty<string>(), Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
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
    services.AddSingleton<AppStateStore>();
    services.AddSingleton<IStartupService, WindowsStartupService>();
    services.AddSingleton<CnIpPool>();
    services.AddSingleton<CryptoService>();
    services.AddSingleton<NetEaseApiClient>();
    // 音源抽象:网易云 + QQ 音乐,MusicApiProvider 按 Song.Source 路由
    services.AddSingleton<QQMusicApiClient>();
    services.AddSingleton<IMusicApi>(sp => sp.GetRequiredService<NetEaseApiClient>());
    services.AddSingleton<IMusicApi>(sp => sp.GetRequiredService<QQMusicApiClient>());
    services.AddSingleton<MusicApiProvider>();
    services.AddSingleton<MusicCacheService>();
    services.AddSingleton<IPlatformShareService, WindowsShareService>();
    // 应用自更新(Velopack + GitHub Releases;开发目录直跑 IsSupported=false)
    services.AddSingleton<IAppUpdateService, VelopackUpdateService>();
#if ANDROID
    services.AddSingleton<AndroidMediaPlayer>();
    services.AddSingleton<IAudioPlayer>(sp => new CrossfadeAudioPlayer(
        sp.GetRequiredService<AndroidMediaPlayer>(),
        () => ActivatorUtilities.CreateInstance<AndroidMediaPlayer>(sp)));
    services.AddSingleton<ISmtcService, SmtcServiceStub>();
#else
    services.AddSingleton<WindowsMediaPlayer>();
    services.AddSingleton<IAudioPlayer>(sp => new CrossfadeAudioPlayer(
        sp.GetRequiredService<WindowsMediaPlayer>(),
        () => ActivatorUtilities.CreateInstance<WindowsMediaPlayer>(sp)));
    services.AddSingleton<ISmtcService, SmtcService>();
#endif
    services.AddSingleton<LyricViewModel>();
    services.AddSingleton<PlayerViewModel>();
    services.AddSingleton<SearchViewModel>();
    services.AddSingleton<PlaylistViewModel>();
    services.AddSingleton<RecommendViewModel>();
    services.AddSingleton<ArtistViewModel>();
    services.AddSingleton<AlbumViewModel>();
    services.AddSingleton<ArtistSongsPageViewModel>();
    services.AddSingleton<ArtistAlbumsPageViewModel>();
    services.AddSingleton<UserProfileViewModel>();
    services.AddSingleton<PersonalHomeViewModel>();
    services.AddSingleton<CollectedPlaylistsViewModel>();
    services.AddSingleton<RecentPlaybackViewModel>();
    services.AddSingleton<SettingsViewModel>();
    services.AddSingleton<AccountViewModel>();
    services.AddSingleton<MainViewModel>();
    ServiceLocator.Provider = services.BuildServiceProvider();
    CoverImagePipeline.Configure(ServiceLocator.Get<MusicCacheService>());

    return AppBuilder.Configure<App>()
      .UsePlatformDetect()
      .WithInterFont()
      .LogToTrace();
  }
}
