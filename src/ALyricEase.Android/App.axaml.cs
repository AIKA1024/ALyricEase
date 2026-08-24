using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ALyricEase.Infrastructure;
using ALyricEase.Services.Audio;
using ALyricEase.Services.Auth;
using ALyricEase.Services.Crypto;
using ALyricEase.Services.NetEase;
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
        services.AddSingleton<CnIpPool>();
        services.AddSingleton<CryptoService>();
        services.AddSingleton<NetEaseApiClient>();
        services.AddSingleton<IAudioPlayer, AndroidMediaPlayer>();
        services.AddSingleton<ISmtcService, SmtcService>();
        services.AddSingleton<LyricViewModel>();
        services.AddSingleton<PlayerViewModel>();
        services.AddSingleton<SearchViewModel>();
        services.AddSingleton<PlaylistViewModel>();
        services.AddSingleton<RecommendViewModel>();
        services.AddSingleton<ArtistViewModel>();
        services.AddSingleton<AlbumViewModel>();
        services.AddSingleton<MainViewModel>();
        ServiceLocator.Provider = services.BuildServiceProvider();
    }
}
