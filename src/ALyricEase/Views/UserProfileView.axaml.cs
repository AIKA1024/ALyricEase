using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ALyricEase.Infrastructure;
using ALyricEase.ViewModels;
using Visual = Avalonia.Visual;

namespace ALyricEase.Views;

public partial class UserProfileView : UserControl
{
    private readonly DetailPageScrollController _scrollController;

    public UserProfileView()
    {
        InitializeComponent();
        _scrollController = new DetailPageScrollController(this, PageScroller);
        SizeChanged += (_, e) => ResponsiveClasses.ApplyByWindow(this);
        AttachedToVisualTree += (_, _) => ResponsiveClasses.ApplyByWindow(this);
        // 英雄卡模糊背景不需要任何尺寸回写:它装在 OverlayPanel(Infrastructure)里,
        // 不参与测量、排列时铺满卡片 —— 容器高度由内容决定,大图结构上撑不爆。
        // 勿改回 SizeChanged 回写方案:回写的"卡片尺寸"正是被图片撑大的值,正反馈冻结。
    }

    /// <summary>Tapped 命中是否来自行内操作按钮(▶/•••):它们的 Click 已自处理,
    /// 但 Tapped 是独立路由事件会继续冒泡 —— 源的最近 Button 祖先不是行自身即排除。
    /// (TrackRow 同款思路;那边行是 TemplatedControl,"任意 Button 祖先"即内部按钮,
    /// 这里行本身是 Button,排除的是"非行 Button"。)</summary>
    private static bool IsInnerButtonHit(object? sender, TappedEventArgs e)
        => e.Source is Visual source
           && source.FindAncestorOfType<Button>() is { } hit
           && !ReferenceEquals(hit, sender);

    /// <summary>音乐品味行(喜欢的音乐/年度歌单) → 歌单详情页。激活走 Tapped 手势层(TrackRow 同款):
    /// 触屏长按弹菜单后手势层不发 Tapped,不会像 Click 那样在松手时补发激活。</summary>
    private void OnTasteRowTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Button { DataContext: UserTasteRowViewModel row } button) return;
        if (IsInnerButtonHit(sender, e)) return;
        ServiceLocator.Get<MainViewModel>().OpenShellPlaylistCommand.Execute(row.Playlist);
    }

    /// <summary>参与创作/收藏的歌单行 → 歌单详情页。激活走 Tapped(TrackRow 同款,见上)。</summary>
    private void OnPlaylistRowTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Button { DataContext: PlaylistItemViewModel pvm }) return;
        if (IsInnerButtonHit(sender, e)) return;
        ServiceLocator.Get<MainViewModel>().OpenShellPlaylistCommand.Execute(pvm);
    }

    /// <summary>宽/中屏卡片悬停浮现的播放圆钮:播放歌单。阻止事件冒泡到卡片按钮(否则会顺带打开详情页)。</summary>
    private void OnCardPlayClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        ResolveCardPlaylist(sender, pvm =>
            ServiceLocator.Get<PlayerViewModel>().PlayPlaylistCommand.Execute(pvm));
    }

    /// <summary>宽/中屏卡片悬停浮现的 ••• 圆钮:原版歌单 more 菜单,贴按钮弹出。</summary>
    private void OnCardMoreClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button button)
            ResolveCardPlaylist(button, pvm => PlaylistContextMenu.Create(button, pvm).ShowAt(button));
    }

    /// <summary>卡片操作按钮的 DataContext:常规歌单卡是 PlaylistItemViewModel,音乐品味卡是它的包装。</summary>
    private static PlaylistItemViewModel? ResolvePlaylistViewModel(object? sender)
    {
        if (sender is not Button { DataContext: { } dc }) return null;
        return dc switch
        {
            PlaylistItemViewModel p => p,
            UserTasteRowViewModel t => t.Playlist,
            _ => null,
        };
    }

    private static void ResolveCardPlaylist(object? sender, Action<PlaylistItemViewModel> action)
    {
        var pvm = ResolvePlaylistViewModel(sender);
        if (pvm is not null) action(pvm);
    }

    /// <summary>歌单行/卡片右键(桌面)或长按(触摸)弹出 more 菜单:与行内 ••• 同一份即时构造,
    /// 贴指针弹出。ContextRequested 由 Avalonia 统一派发 —— 右键与触屏长按共用这一个入口,
    /// 不用 ContextFlyout 静态实例(会对所有行共用一份,绑定错行,侧栏菜单同款教训)。
    /// 行激活走 Tapped 手势层(见 OnPlaylistRowTapped):长按松手手势层不发 Tapped,无补发激活问题。</summary>
    private void OnPlaylistContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Button button) return;
        var pvm = ResolvePlaylistViewModel(button);
        if (pvm is null) return;
        e.Handled = true;
        PlaylistContextMenu.Create(button, pvm).ShowAt(button, true);
    }

    /// <summary>窄屏行内 ▶:播放歌单(打开详情页由行本身承担)。品味行/普通歌单行共用。</summary>
    private void OnRowPlayClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        ResolveCardPlaylist(sender, pvm =>
            ServiceLocator.Get<PlayerViewModel>().PlayPlaylistCommand.Execute(pvm));
    }

    /// <summary>窄屏行内 •••:原版歌单 more 菜单(播放/收藏/创建者/分享/复制链接),贴按钮弹出。</summary>
    private void OnRowMoreClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button button)
            ResolveCardPlaylist(button, pvm => PlaylistContextMenu.Create(button, pvm).ShowAt(button));
    }
}
