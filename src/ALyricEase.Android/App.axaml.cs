using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
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

namespace ALyricEase;

public partial class App : Avalonia.Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // 触控为主的输入模式:必须在任何视图创建前声明(TrackRow 构造按此订阅单击播放/挂 touch 类)
        InteractionDefaults.Init(touchPrimary: true);

        // 部分 Android Automotive ROM 的 ClipDescription 缺少标准 text/* MIME；
        // Avalonia 因而不暴露 DataFormat.Text。仅在标准读取为空时直接 CoerceToText。
        ClipboardService.RegisterTextReaderFallback(AndroidClipboardTextReader.TryGetTextAsync);

        ConfigureServices();

        if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            singleView.MainView = new MainView
            {
                DataContext = ServiceLocator.Get<MainViewModel>(),
            };
        }

        // 初始化媒体会话服务(显示状态栏媒体通知)
        ServiceLocator.Get<ISmtcService>().Initialize(IntPtr.Zero);

        // 与桌面端一致:启动时若已存 MUSIC_U 则恢复登录态与歌单(失败静默,不阻塞启动)
        _ = RestoreLoginAsync();

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

    private static void ConfigureServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<DispatcherService>();
        services.AddSingleton<CookieStore>();
        // 主窗口几何 + 侧栏分组折叠状态持久化(MainViewModel 构造注入)
        services.AddSingleton<AppStateStore>();
        services.AddSingleton<CnIpPool>();
        services.AddSingleton<CryptoService>();
        services.AddSingleton<NetEaseApiClient>();
        // 音源抽象:网易云 + QQ 音乐,MusicApiProvider 按 Song.Source 路由
        services.AddSingleton<QQMusicApiClient>();
        services.AddSingleton<IMusicApi>(sp => sp.GetRequiredService<NetEaseApiClient>());
        services.AddSingleton<IMusicApi>(sp => sp.GetRequiredService<QQMusicApiClient>());
        services.AddSingleton<MusicApiProvider>();
        services.AddSingleton<MusicCacheService>();
        services.AddSingleton<IAudioPlayer, AndroidMediaPlayer>();
        services.AddSingleton<ISmtcService, SmtcService>();
        services.AddSingleton<LyricViewModel>();
        services.AddSingleton<PlayerViewModel>();
        services.AddSingleton<SearchViewModel>();
        services.AddSingleton<PlaylistViewModel>();
        services.AddSingleton<RecommendViewModel>();
        services.AddSingleton<ArtistViewModel>();
        services.AddSingleton<AlbumViewModel>();
        services.AddSingleton<ArtistSongsPageViewModel>();
        services.AddSingleton<ArtistAlbumsPageViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<AccountViewModel>();
        services.AddSingleton<MainViewModel>();
        ServiceLocator.Provider = services.BuildServiceProvider();
    }
}
