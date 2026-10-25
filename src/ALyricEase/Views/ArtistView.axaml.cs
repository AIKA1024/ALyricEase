using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ALyricEase.Infrastructure;
using ALyricEase.Services;
using ALyricEase.ViewModels;

namespace ALyricEase.Views;

public partial class ArtistView : UserControl
{
    private readonly DetailPageScrollController _scrollController;

    public ArtistView()
    {
        InitializeComponent();
        _scrollController = new DetailPageScrollController(this, PageScroller);
        SizeChanged += (_, e) => ResponsiveClasses.ApplyByWindow(this);
        AttachedToVisualTree += (_, _) => ResponsiveClasses.ApplyByWindow(this);
    }

    /// <summary>头部"更多"菜单共用的歌手公开链接;页面还没加载出歌手时为 null。</summary>
    private string? ShareUrl => (DataContext as ArtistViewModel)?.ShareUrl;

    private async void OnShareArtistClick(object? sender, RoutedEventArgs e)
    {
        if (ShareUrl is not { } link || DataContext is not ArtistViewModel viewModel) return;
        try
        {
            var ownerHandle = TopLevel.GetTopLevel(this)?.TryGetPlatformHandle()?.Handle ?? 0;
            await ServiceLocator.Get<IPlatformShareService>().ShareUriAsync(
                ownerHandle, $"分享歌手：{viewModel.Name}", viewModel.Name, new Uri(link));
        }
        catch
        {
            // 无头测试或宿主不支持系统分享时保持页面状态,复制链接入口仍可用。
        }
    }

    private async void OnOpenArtistInBrowserClick(object? sender, RoutedEventArgs e)
    {
        if (ShareUrl is not { } link) return;
        try
        {
            if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
                await launcher.LaunchUriAsync(new Uri(link));
        }
        catch
        {
            // 平台没有可处理该链接的应用时静默返回。
        }
    }

    private async void OnCopyArtistLinkClick(object? sender, RoutedEventArgs e)
    {
        if (ShareUrl is { } link)
            await ClipboardService.TryCopyTextAsync(link);
    }
}
