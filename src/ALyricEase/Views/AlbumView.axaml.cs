using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ALyricEase.Infrastructure;
using ALyricEase.Services;
using ALyricEase.ViewModels;

namespace ALyricEase.Views;

public partial class AlbumView : UserControl
{
    private readonly DetailPageScrollController _scrollController;

    public AlbumView()
    {
        InitializeComponent();
        _scrollController = new DetailPageScrollController(this, PageScroller);
        SizeChanged += (_, e) => ResponsiveClasses.ApplyByWindow(this);
        AttachedToVisualTree += (_, _) => ResponsiveClasses.ApplyByWindow(this);
    }

    private void OnArtistButtonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button ||
            button.DataContext is not AlbumViewModel { HasMultipleArtists: true }) return;
        if (button.Resources["ArtistMenuFlyout"] is not MenuFlyout flyout) return;
        flyout.ShowAt(button);
        e.Handled = true;
    }

    /// <summary>头部「更多 → 分享」:系统分享面板(和歌单页同一个服务、同一种文案口径)。
    /// 无头/宿主不支持分享时静默保持页面状态。</summary>
    private async void OnShareAlbumClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AlbumViewModel { ShareLink.Length: > 0 } vm) return;
        try
        {
            var ownerHandle = TopLevel.GetTopLevel(this)?.TryGetPlatformHandle()?.Handle ?? 0;
            var description = vm.ArtistName.Length > 0 ? vm.ArtistName : vm.Name;
            await ServiceLocator.Get<IPlatformShareService>().ShareUriAsync(
                ownerHandle, $"分享专辑：{vm.Name}", description, new Uri(vm.ShareLink));
        }
        catch
        {
            // 分享失败不影响页面
        }
    }

    private async void OnCopyAlbumLinkClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AlbumViewModel { ShareLink.Length: > 0 } vm) return;
        await ClipboardService.TryCopyTextAsync(vm.ShareLink);
    }

    /// <summary>容器 realized 时加载红心状态；封面由 Image 按可见性加载。</summary>
    private void OnTrackContainerPreparing(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container?.DataContext is SongItemViewModel song)
            song.EnsureLikedLoaded();
    }
}
