using Avalonia.Controls;
using Avalonia.Interactivity;
using ALyricEase.Infrastructure;
using ALyricEase.ViewModels;

namespace ALyricEase.Views;

/// <summary>「我的收藏」页:网易云里收藏的他人歌单列表。点击行进歌单详情,
/// 悬停浮现打开/更多(••• 弹 PlaylistContextMenu)。</summary>
public partial class CollectedPlaylistsView : UserControl
{
    public CollectedPlaylistsView()
    {
        InitializeComponent();
        // 空态开关:集合为空时显示引导文案。DataContext 先于订阅就绪时立即算一次。
        DataContextChanged += (_, _) =>
        {
            UpdateEmptyState();
            if (DataContext is CollectedPlaylistsViewModel vm)
                vm.Playlists.CollectionChanged += (_, _) => UpdateEmptyState();
        };
        UpdateEmptyState();
    }

    private void UpdateEmptyState() => EmptyState.IsVisible = Rows.ItemCount == 0;

    /// <summary>行/行内 ▶:▶ 播放歌单(共用 PlayerViewModel.PlayPlaylist);点击行其余区域进歌单详情。</summary>
    private void OnPlaylistRowClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PlaylistItemViewModel pvm })
            ServiceLocator.Get<MainViewModel>().OpenShellPlaylistCommand.Execute(pvm);
    }

    private void OnRowPlayClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { DataContext: PlaylistItemViewModel pvm })
            ServiceLocator.Get<PlayerViewModel>().PlayPlaylistCommand.Execute(pvm);
    }

    /// <summary>行内 •••:原版歌单 more 菜单(播放/收藏/创建者/分享/复制链接)。</summary>
    private void OnRowMoreClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { DataContext: PlaylistItemViewModel pvm } button)
            PlaylistContextMenu.Create(button, pvm).ShowAt(button);
    }
}
