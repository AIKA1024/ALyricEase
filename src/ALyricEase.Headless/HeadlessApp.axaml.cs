using Avalonia;
using Avalonia.Markup.Xaml;
using ALyricEase.Infrastructure;
using ALyricEase.Services;
using ALyricEase.Services.Audio;
using ALyricEase.Services.Auth;
using ALyricEase.Services.Crypto;
using ALyricEase.Services.NetEase;
using ALyricEase.Services.QQMusic;
using ALyricEase.Services.Smtc;
using ALyricEase.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace ALyricEase.Headless;

public partial class HeadlessApp : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>无头播放器桩:满足 DI,不做任何事。</summary>
    private sealed class StubAudioPlayer : IAudioPlayer
  {
    public PlaybackState State => PlaybackState.Idle;
    public long PositionMs { get; set; }
    public long DurationMs => 0;
    public int Volume { get; set; }
    public event EventHandler? StateChanged;
    public event EventHandler<long>? PositionChanged;
    public event EventHandler<long>? DurationChanged;
    public event EventHandler<string>? ErrorOccurred;
    public void PlayUrl(string url) { }
    public void Pause() { }
    public void Resume() { }
    public void Stop() { }
    public void Dispose() { }
  }

    public static void ConfigureServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<DispatcherService>();
        services.AddSingleton<CookieStore>();
        // MainViewModel 构造注入的状态持久化存储(探测程序解析 MainViewModel 需要)
        services.AddSingleton<AppStateStore>();
        services.AddSingleton<CnIpPool>();
        services.AddSingleton<CryptoService>();
        // 离线探针假音源(Source=99):LyricSwitchProbe 等用不触网的歌曲驱动生产播放/歌词链路
        services.AddSingleton<IMusicApi>(new OfflineProbeApi());
        services.AddSingleton<NetEaseApiClient>();
        services.AddSingleton<QQMusicApiClient>();
        services.AddSingleton<IMusicApi>(sp => sp.GetRequiredService<NetEaseApiClient>());
        services.AddSingleton<IMusicApi>(sp => sp.GetRequiredService<QQMusicApiClient>());
        services.AddSingleton<MusicApiProvider>();
        services.AddSingleton<MusicCacheService>();
        // 歌词 VM 用独立临时目录缓存,避免探针歌曲写进真实用户缓存
        services.AddSingleton<LyricViewModel>(sp => new LyricViewModel(
            new MusicApiProvider(new IMusicApi[] { new OfflineProbeApi() }),
            sp.GetRequiredService<DispatcherService>(),
            new MusicCacheService(64, Path.Combine(Path.GetTempPath(), "aly-probe-lyrics"), new HttpClient())));
        services.AddSingleton<IAudioPlayer, StubAudioPlayer>();
        services.AddSingleton<ISmtcService, SmtcServiceStub>();
        services.AddSingleton<LyricViewModel>();
        services.AddSingleton<PlayerViewModel>();
        services.AddSingleton<SearchViewModel>();
        services.AddSingleton<PlaylistViewModel>();
        services.AddSingleton<RecommendViewModel>();
        services.AddSingleton<ArtistViewModel>();
        services.AddSingleton<AlbumViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<AccountViewModel>();
        services.AddSingleton<MainViewModel>();
        ServiceLocator.Provider = services.BuildServiceProvider();
    }
}
