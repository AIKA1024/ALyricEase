using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ALyricEase.Infrastructure;
using ALyricEase.ViewModels;
using Visual = Avalonia.Visual;

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

    /// <summary>行/行内 ▶:▶ 播放歌单(共用 PlayerViewModel.PlayPlaylist);点击行其余区域进歌单详情。
    /// 行激活走 Tapped 手势层(TrackRow 同款):触屏长按弹菜单后手势层不发 Tapped,
    /// 不会像 Click 那样在松手时补发激活。</summary>
    private void OnPlaylistRowTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Button { DataContext: PlaylistItemViewModel pvm } button) return;
        // 排除行内操作按钮(▶/•••)的命中:Tapped 独立于 Click 冒泡,源的最近
        // Button 祖先不是行自身即来自内部按钮(行本身是 Button,排除"非行 Button")
        if (e.Source is Visual source
            && source.FindAncestorOfType<Button>() is { } hit
            && !ReferenceEquals(hit, button)) return;
        ServiceLocator.Get<MainViewModel>().OpenShellPlaylistAuto(pvm);
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

    /// <summary>行右键(桌面)或长按(触摸)弹出同一份 more 菜单,贴指针弹出;
    /// ContextRequested 由 Avalonia 统一派发,右键与触屏长按共用这一个入口
    /// (不用 ContextFlyout 静态实例:会对所有行共用一份,绑定错行)。
    /// 行激活走 Tapped 手势层(见 OnPlaylistRowTapped),长按松手无补发激活问题。</summary>
    private void OnPlaylistContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Button { DataContext: PlaylistItemViewModel pvm } button) return;
        e.Handled = true;
        PlaylistContextMenu.Create(button, pvm).ShowAt(button, true);
    }
}
