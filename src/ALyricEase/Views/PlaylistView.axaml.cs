using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ALyricEase.Infrastructure;
using ALyricEase.Services;
using ALyricEase.ViewModels;

namespace ALyricEase.Views;

/// <summary>歌单详情页：登录卡片 / 选中歌单的头部 + 曲目表。歌单入口由左侧 shell 提供。</summary>
public partial class PlaylistView : UserControl
{
    private DispatcherTimer? _coverDebounce;
    private readonly System.Collections.Generic.HashSet<SongItemViewModel> _pendingCovers = new();

    public PlaylistView()
    {
        InitializeComponent();
        SizeChanged += (_, e) => ResponsiveClasses.ApplyByWindow(this);
        AttachedToVisualTree += (_, _) => ResponsiveClasses.ApplyByWindow(this);
        // 页面滚动接近底部时增量补齐下一批曲目
        PageScroller.ScrollChanged += OnTracksScrollChanged;
    }

    /// <summary>滚动接近底部(剩余不足 ~500px)时请求下一批曲目元数据;LoadMoreAsync 内部单飞防止重入。</summary>
    private void OnTracksScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        var remaining = sv.Extent.Height - sv.Offset.Y - sv.Viewport.Height;
        if (remaining < 500 && DataContext is PlaylistViewModel vm)
            _ = vm.LoadMoreAsync();
    }

    /// <summary>曲目容器 realized 时触发封面懒加载。封面节流:滚动中不立即加载,
    /// 停止 150ms 后统一加载可见行,减少滚动时封面解码/Image 更新造成的渲染与内存波动。</summary>
    private void OnTrackContainerPreparing(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container?.DataContext is not SongItemViewModel item) return;
        _coverDebounce ??= new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, OnCoverDebounceTick);
        _coverDebounce.Stop();
        _pendingCovers.Add(item);
        _coverDebounce.Start();
    }

    private void OnCoverDebounceTick(object? sender, EventArgs e)
    {
        _coverDebounce?.Stop();
        foreach (var item in _pendingCovers) item.EnsureLikedLoaded();
        _pendingCovers.Clear();
    }

    /// <summary>原版“更多”菜单的系统分享入口。原生分享面板需要当前 TopLevel 句柄，因此留在视图层。</summary>
    private async void OnSharePlaylistClick(object? sender, RoutedEventArgs e)
    {
        if (GetShareablePlaylist() is not { } playlist) return;
        try
        {
            var link = PlaylistShareLinks.For(playlist);
            var ownerHandle = TopLevel.GetTopLevel(this)?.TryGetPlatformHandle()?.Handle ?? 0;
            var description = string.IsNullOrWhiteSpace(playlist.Description)
                ? playlist.Name
                : playlist.Description;
            await ServiceLocator.Get<IPlatformShareService>().ShareUriAsync(
                ownerHandle, $"分享歌单：{playlist.Name}", description, new Uri(link));
        }
        catch
        {
            // 无头测试或当前宿主不支持系统分享时保持页面状态。
        }
    }

    private async void OnCopyPlaylistLinkClick(object? sender, RoutedEventArgs e)
    {
        if (GetShareablePlaylist() is not { } playlist) return;
        await ClipboardService.TryCopyTextAsync(PlaylistShareLinks.For(playlist));
    }

    private Models.Playlist? GetShareablePlaylist()
    {
        if (DataContext is not PlaylistViewModel { SelectedPlaylist.Playlist: { Id: > 0 } playlist })
            return null;
        return playlist;
    }
}
