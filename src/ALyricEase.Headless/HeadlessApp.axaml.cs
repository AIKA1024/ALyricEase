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

    /// <summary>无头播放器桩:满足 DI,默认不做任何事。
    /// 探针可用 SetState/RaisePosition 手动驱动状态与进度事件 —— 这样走的是
    /// PlayerViewModel 真实订阅链路(OnStateChanged/OnPositionChanged),而不是直接改 VM 属性。</summary>
    internal sealed class StubAudioPlayer : IAudioPlayer
  {
    private PlaybackState _state = PlaybackState.Idle;

    public PlaybackState State => _state;
    public long PositionMs { get; set; }
    public long DurationMs { get; set; }
    public int Volume { get; set; }
    public event EventHandler? StateChanged;
    public event EventHandler<long>? PositionChanged;
    public event EventHandler<long>? DurationChanged;
    public event EventHandler<string>? ErrorOccurred;
    public void PlayUrl(string url) { }
    public void Pause() { }
    public void Resume() { }
    public void Stop() => StopCallCount++;
    public void Dispose() { }

    /// <summary>Stop 被调用次数(交叉淡化探针用:验证渐变结束后旧实例会被停掉)。</summary>
    public int StopCallCount { get; private set; }

    // ---- 音频输出设备桩(--audiodevice 探针用:两条假设备 + 可配置的支持位/失败位)----

    private IReadOnlyList<AudioOutputDevice> _outputDevices = Array.Empty<AudioOutputDevice>();

    /// <summary>关掉可模拟"后端不支持切换设备"(Android 老版本/无头)那条分支。</summary>
    public bool DeviceSelectionSupported { get; set; } = true;

    /// <summary>把这个 Id 设为一次"应用失败",模拟设备被占用 —— 验证设置页会不会静默留假状态。</summary>
    public string? FailDeviceId { get; set; }

    /// <summary>TrySetOutputDevice 被调用的次数(含传 null 的"回到默认")。</summary>
    public int SetDeviceCallCount { get; private set; }

    public bool SupportsOutputDeviceSelection => DeviceSelectionSupported;

    public IReadOnlyList<AudioOutputDevice> OutputDevices => _outputDevices;

    public string? OutputDeviceId { get; private set; }

    public void SetOutputDevices(params AudioOutputDevice[] devices) => _outputDevices = devices;

    public Task<IReadOnlyList<AudioOutputDevice>> RefreshOutputDevicesAsync() =>
        Task.FromResult(_outputDevices);

    public bool TrySetOutputDevice(string? deviceId)
    {
      SetDeviceCallCount++;
      if (deviceId is not null && deviceId == FailDeviceId) return false;
      OutputDeviceId = deviceId;
      return true;
    }

    /// <summary>切换播放状态并通知订阅者(等价于真实播放引擎的状态回调)。</summary>
    public void SetState(PlaybackState state)
    {
      _state = state;
      StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>上报一次播放进度(等价于真实播放引擎的周期 PositionChanged)。</summary>
    public void RaisePosition(long positionMs)
    {
      PositionMs = positionMs;
      PositionChanged?.Invoke(this, positionMs);
    }
  }

    public static void ConfigureServices(Action<IServiceCollection>? configure = null)
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
        services.AddSingleton<IPlatformShareService, PlatformShareServiceStub>();
        // 无头探针无自更新能力:注册空实现占位(SettingsViewModel 构造解析需要)
        services.AddSingleton<ALyricEase.Services.Update.IAppUpdateService,
            ALyricEase.Services.Update.NullAppUpdateService>();
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
        // 二级页 VM:MainViewModel 构造注入,单独探针(专辑卡片几何等)也会直接取用
        services.AddSingleton<ArtistSongsPageViewModel>();
        services.AddSingleton<ArtistAlbumsPageViewModel>();
        services.AddSingleton<RecentPlaybackViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<AccountViewModel>();
        // 用户页/收藏页 VM:MainViewModel 构造注入(此前缺失,凡取 MainViewModel 的探针都会解析失败)
        services.AddSingleton<UserProfileViewModel>();
        services.AddSingleton<PersonalHomeViewModel>();
        services.AddSingleton<CollectedPlaylistsViewModel>();
        services.AddSingleton<MainViewModel>();
        configure?.Invoke(services);
        ServiceLocator.Provider = services.BuildServiceProvider();
        CoverImagePipeline.Configure(ServiceLocator.Get<MusicCacheService>());
    }
}
