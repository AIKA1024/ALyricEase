using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
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
    private PlaylistViewModel? _observedViewModel;
    private int _appliedScrollRestoreVersion;
    private int _scheduledScrollRestoreVersion;
    private bool _isAttached;

    public PlaylistView()
    {
        InitializeComponent();
        SizeChanged += (_, e) => ResponsiveClasses.ApplyByWindow(this);
        AttachedToVisualTree += (_, _) =>
        {
            _isAttached = true;
            ResponsiveClasses.ApplyByWindow(this);
            ObserveViewModel();
            ScheduleScrollRestore();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _isAttached = false;
            _scheduledScrollRestoreVersion = 0;
            StopObservingViewModel();
        };
        DataContextChanged += (_, _) =>
        {
            if (!_isAttached) return;
            ObserveViewModel();
            ScheduleScrollRestore();
        };
        // 页面滚动接近底部时增量补齐下一批曲目
        PageScroller.ScrollChanged += OnTracksScrollChanged;
    }

    /// <summary>滚动接近底部(剩余不足 ~500px)时请求下一批曲目元数据;LoadMoreAsync 内部单飞防止重入。</summary>
    private void OnTracksScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        if (DataContext is PlaylistViewModel current)
            current.UpdatePageScrollOffset(sv.Offset.Y);
        var remaining = sv.Extent.Height - sv.Offset.Y - sv.Viewport.Height;
        if (remaining < 500 && DataContext is PlaylistViewModel vm)
            _ = vm.LoadMoreAsync();
    }

    private void ObserveViewModel()
    {
        var next = DataContext as PlaylistViewModel;
        if (ReferenceEquals(next, _observedViewModel)) return;
        StopObservingViewModel();
        _observedViewModel = next;
        if (next is not null) next.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void StopObservingViewModel()
    {
        if (_observedViewModel is not null)
            _observedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _observedViewModel = null;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlaylistViewModel.ScrollRestoreVersion))
            ScheduleScrollRestore();
    }

    private void ScheduleScrollRestore()
    {
        if (DataContext is not PlaylistViewModel vm
            || !vm.TryGetPendingScrollRestore(
                _appliedScrollRestoreVersion, out var version, out var offset)
            || version == _scheduledScrollRestoreVersion)
            return;

        _scheduledScrollRestoreVersion = version;
        RestoreScrollAfterLayout(vm, version, offset, attempt: 0);
    }

    private void RestoreScrollAfterLayout(
        PlaylistViewModel vm, int version, double offset, int attempt)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(DataContext, vm)
                || !_isAttached
                || !vm.TryGetPendingScrollRestore(
                    _appliedScrollRestoreVersion, out var latestVersion, out var latestOffset)
                || latestVersion != version)
                return;

            PageScroller.UpdateLayout();
            var maximum = Math.Max(0, PageScroller.Extent.Height - PageScroller.Viewport.Height);
            if (maximum + 1 < latestOffset && attempt < 3)
            {
                RestoreScrollAfterLayout(vm, version, latestOffset, attempt + 1);
                return;
            }

            PageScroller.Offset = new(PageScroller.Offset.X, Math.Min(latestOffset, maximum));
            _appliedScrollRestoreVersion = version;
            _scheduledScrollRestoreVersion = 0;
        }, attempt == 0 ? DispatcherPriority.Loaded : DispatcherPriority.Render);
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

    /// <summary>本地音乐歌单导入歌曲:文件选择器多选音频,路径交给 VM 持久化并追加显示行。</summary>
    private async void OnImportLocalTracksClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;
        if (DataContext is not PlaylistViewModel { IsLocalPlaylist: true } viewModel) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择要导入的本地歌曲",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("音频文件")
                {
                    Patterns = LocalAudioFiles.SupportedExtensions.Select(ext => "*" + ext).ToList(),
                },
            ],
        });
        if (files.Count == 0) return;

        var paths = new List<string>();
        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (!string.IsNullOrEmpty(path)) paths.Add(path);
        }
        viewModel.AddLocalTracks(paths);
    }

    /// <summary>本地音乐歌单导入文件夹:选一个目录,递归枚举其中受支持的音频文件。</summary>
    private async void OnImportLocalFolderClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;
        if (DataContext is not PlaylistViewModel { IsLocalPlaylist: true } viewModel) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择要导入的音乐文件夹",
            AllowMultiple = false,
        });
        if (folders.Count == 0) return;

        var path = folders[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;
        viewModel.AddLocalTracks([path]);
    }
}
