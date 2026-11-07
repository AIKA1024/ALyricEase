using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ALyricEase.Infrastructure;
using ALyricEase.ViewModels;

namespace ALyricEase.Views;

public partial class RecommendView : UserControl
{
    public RecommendView()
    {
        InitializeComponent();
        SizeChanged += (_, e) => ResponsiveClasses.ApplyByWindow(this);
        AttachedToVisualTree += (_, _) => ResponsiveClasses.ApplyByWindow(this);
    }

    /// <summary>卡片容器 realized 后延迟提交封面 URL，避免切页动画首帧解码拥塞。</summary>
    private void OnItemContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container.DataContext is RecommendCardViewModel card)
        {
            // 真实封面延后到切页过渡结束后再亮出(Background 优先级),避免 30 张位图首帧绘制卡死过渡
            card.PrepareCoverForTransition();
        }
    }

    /// <summary>歌单卡封面上的 ▶:播放整张歌单(歌曲卡点击即播放,没有这个圆钮)。
    /// Handled 阻止 Click 继续冒泡,避免顺带触发卡片的打开命令。</summary>
    private void OnCardPlayClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { DataContext: RecommendCardViewModel { Playlist: { } playlist } })
            ServiceLocator.Get<PlayerViewModel>().PlayPlaylistCommand.Execute(playlist);
    }

    /// <summary>歌单卡封面上的 •••:原版歌单 more 菜单(播放/收藏/创建者/分享/复制链接),贴按钮弹出。</summary>
    private void OnCardMoreClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { DataContext: RecommendCardViewModel { Playlist: { } playlist } } button)
            PlaylistContextMenu.Create(button, playlist).ShowAt(button);
    }

    /// <summary>歌单卡右键(桌面)/长按(触摸)弹出同一份 more 菜单,贴指针弹出。
    /// 菜单每次即时构造 —— 不用 ContextFlyout 静态实例(所有卡片共用一份会绑定错行)。
    /// 歌曲卡没有歌单数据,不弹菜单。</summary>
    private void OnCardContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Button { DataContext: RecommendCardViewModel { Playlist: { } playlist } } button) return;
        e.Handled = true;
        PlaylistContextMenu.Create(button, playlist).ShowAt(button, true);
    }
}
