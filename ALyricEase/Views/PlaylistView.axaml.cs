using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;
using ALyricEase.ViewModels;

namespace ALyricEase.Views;

/// <summary>歌单页:登录卡片 / 用户歌单 + 歌单详情(头部 260 + 曲目表)。曲目分页懒加载 + 封面按可见加载。</summary>
public partial class PlaylistView : UserControl
{
    private const double WideThreshold = 460;

    public PlaylistView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (TracksList.FindDescendantOfType<ScrollViewer>() is { } sv)
                sv.ScrollChanged += OnTracksScrollChanged;
            ApplyHeaderLayout(TrackHeader.Bounds.Width);
        };
        TrackHeader.SizeChanged += OnTrackHeaderSizeChanged;
    }

    /// <summary>双击左歌单 → 打开曲目。</summary>
    private void OnPlaylistDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is PlaylistViewModel vm && vm.SelectedPlaylist is { } p)
            vm.OpenPlaylistCommand.Execute(p);
    }

    private void OnPlaylistContainerPreparing(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container?.DataContext is PlaylistItemViewModel item)
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

    private void OnTrackHeaderSizeChanged(object? sender, SizeChangedEventArgs e)
        => ApplyHeaderLayout(e.NewSize.Width);

    /// <summary>列头与 TrackListItem 同步:窄窗收起专辑/时长列(同为窄阈值)。</summary>
    private void ApplyHeaderLayout(double width)
    {
        var narrow = width > 0 && width < WideThreshold;
        // Avalonia 命名 ColumnDefinition 不会生成字段 → 经 Grid.ColumnDefinitions 访问
        TrackHeader.ColumnDefinitions[2].Width = new GridLength(narrow ? 0 : 180);
        TrackHeader.ColumnDefinitions[3].Width = new GridLength(narrow ? 0 : 100);
        HeaderAlbum.IsVisible = !narrow;
        HeaderDuration.IsVisible = !narrow;
    }
}
