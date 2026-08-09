using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;
using ALyricEase.ViewModels;

namespace ALyricEase.Views;

/// <summary>歌单页:登录卡片 / 用户歌单 + 曲目(双击播放)。曲目分页懒加载 + 封面按可见加载。</summary>
public partial class PlaylistView : UserControl
{
    public PlaylistView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (TracksList.FindDescendantOfType<ScrollViewer>() is { } sv)
                sv.ScrollChanged += OnTracksScrollChanged;
        };
    }

    private void OnPlaylistDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is PlaylistViewModel vm && vm.SelectedPlaylist is { } p)
            vm.OpenPlaylistCommand.Execute(p);
    }

    private void OnTracksDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is PlaylistViewModel vm && vm.SelectedTrack is { } track)
            track.PlayCommand.Execute(null);
    }

    private void OnPlaylistContainerPreparing(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container?.DataContext is PlaylistItemViewModel item)
            item.EnsureCoverLoaded();
    }

    private void OnTrackContainerPreparing(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container?.DataContext is SongItemViewModel item)
            item.EnsureCoverLoaded();
    }

    private void OnTracksScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        // 接近底部(剩余不足 2 屏)时物化下一批曲目
        if (e.Source is not ScrollViewer sv) return;
        var remaining = sv.Extent.Height - sv.Offset.Y - sv.Viewport.Height;
        if (remaining < 240 && DataContext is PlaylistViewModel vm)
            vm.LoadMoreAsync();
    }
}
