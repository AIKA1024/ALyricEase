using Avalonia.Controls;
using Avalonia.Interactivity;
using ALyricEase.Infrastructure;
using ALyricEase.ViewModels;

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

    /// <summary>音乐品味行(喜欢的音乐/年度歌单) → 歌单详情页。</summary>
    private void OnTasteRowClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: UserTasteRowViewModel row })
            ServiceLocator.Get<MainViewModel>().OpenShellPlaylistCommand.Execute(row.Playlist);
    }

    /// <summary>参与创作/收藏的歌单行 → 歌单详情页。</summary>
    private void OnPlaylistRowClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PlaylistItemViewModel pvm })
            ServiceLocator.Get<MainViewModel>().OpenShellPlaylistCommand.Execute(pvm);
    }

    /// <summary>宽/中屏卡片悬停浮现的播放圆钮:播放歌单。阻止事件冒泡到卡片按钮(否则会顺带打开详情页)。</summary>
    private void OnCardPlayClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        ResolveCardPlaylist(sender, pvm =>
            ServiceLocator.Get<UserProfileViewModel>().PlayPlaylistCommand.Execute(pvm));
    }

    /// <summary>宽/中屏卡片悬停浮现的 ••• 圆钮:原版歌单 more 菜单,贴按钮弹出。</summary>
    private void OnCardMoreClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button button)
            ResolveCardPlaylist(button, pvm => PlaylistContextMenu.Create(button, pvm).ShowAt(button));
    }

    /// <summary>卡片操作按钮的 DataContext:常规歌单卡是 PlaylistItemViewModel,音乐品味卡是它的包装。</summary>
    private static void ResolveCardPlaylist(object? sender, Action<PlaylistItemViewModel> action)
    {
        if (sender is not Button { DataContext: { } dc }) return;
        var pvm = dc switch
        {
            PlaylistItemViewModel p => p,
            UserTasteRowViewModel t => t.Playlist,
            _ => null,
        };
        if (pvm is not null) action(pvm);
    }

    /// <summary>窄屏行内 ▶:播放歌单(打开详情页由行本身承担)。</summary>
    private void OnRowPlayClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { DataContext: PlaylistItemViewModel pvm })
            ServiceLocator.Get<UserProfileViewModel>().PlayPlaylistCommand.Execute(pvm);
    }

    /// <summary>窄屏行内 •••:原版歌单 more 菜单(播放/收藏/创建者/分享/复制链接),贴按钮弹出。</summary>
    private void OnRowMoreClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { DataContext: PlaylistItemViewModel pvm } button)
            PlaylistContextMenu.Create(button, pvm).ShowAt(button);
    }
}
