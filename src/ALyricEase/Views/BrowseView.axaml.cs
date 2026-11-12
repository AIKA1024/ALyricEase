using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ALyricEase.Infrastructure;
using ALyricEase.ViewModels;

namespace ALyricEase.Views;

public partial class BrowseView : UserControl
{
    public BrowseView()
    {
        InitializeComponent();
        // 浏览页视图每次切回都会新建,滚动位置靠 VM 里的快照恢复(详见 BrowseViewModel.RestoreScrollPosition)。
        _ = new DetailPageScrollController(this, PageScroller);
        SizeChanged += (_, _) => ResponsiveClasses.ApplyByWindow(this);
        AttachedToVisualTree += (_, _) => ResponsiveClasses.ApplyByWindow(this);
    }

    /// <summary>卡片容器 realized 后延迟提交封面 URL，避免切页动画首帧解码拥塞
    /// （与个性推荐同一套：过渡 300ms 后再亮封面）。</summary>
    private void OnCardContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container.DataContext is BrowseCardViewModel card)
            card.PrepareCoverForTransition();
    }

    /// <summary>封面上的 ▶:歌单/榜单播整张歌单(榜单走 TopId 分支),单曲播这一首,专辑播这批采样曲目。
    /// Handled 阻止 Click 继续冒泡,避免顺带触发卡片的打开命令(专辑卡点击是进专辑页)。</summary>
    private async void OnCardPlayClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { DataContext: BrowseCardViewModel card }) return;
        if (card.Playlist is { } playlist)
        {
            ServiceLocator.Get<PlayerViewModel>().PlayPlaylistCommand.Execute(playlist);
            return;
        }
        if (card.PlayAction is { } play)
        {
            try { await play(); }
            catch { /* 播放失败:播放器自己提示,卡片不额外弹窗。 */ }
        }
    }

    /// <summary>封面上的 •••:歌单卡弹歌单菜单,单曲卡弹歌曲菜单,专辑卡弹专辑菜单,贴按钮弹出。</summary>
    private void OnCardMoreClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { DataContext: BrowseCardViewModel card } button)
            ShowCardMenu(button, card, fromContextRequest: false);
    }

    /// <summary>右键(桌面)或长按(触摸):与 ••• 同一份即时构造的菜单,贴指针弹出。</summary>
    private void OnCardContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Button { DataContext: BrowseCardViewModel card } button) return;
        if (card.Playlist is null && card.Song is null && card.AlbumSongs is null) return;
        e.Handled = true;
        ShowCardMenu(button, card, fromContextRequest: true);
    }

    /// <summary>三类卡片各有一份即时构造的菜单:歌单(播放/收藏/创建者/分享)、
    /// 歌曲(加歌单/下载/表演者/专辑/分享)、专辑(播放这批新歌/打开专辑页)。</summary>
    private static void ShowCardMenu(Button button, BrowseCardViewModel card, bool fromContextRequest)
    {
        if (card.Playlist is { } playlist)
        {
            PlaylistContextMenu.Create(button, playlist).ShowAt(button, fromContextRequest);
            return;
        }
        if (card.Song is { } song)
        {
            SongContextMenu.Create(button, song).ShowAt(button, fromContextRequest);
            return;
        }
        if (card.AlbumSongs is { Count: > 0 } songs)
            AlbumCardContextMenu.Create(button, card, songs).ShowAt(button, fromContextRequest);
    }

    /// <summary>指针压在海报上就把倒计时冻住(微软商店同款:想让这一张多停一会儿,不用去点箭头)。
    /// 用 Moved 一起兜住"进页面时指针本来就在海报上"的情况 —— 那种情况不会有 Entered。</summary>
    private void OnHeroPointerOver(object? sender, PointerEventArgs e) => HeroRing.IsPaused = true;

    private void OnHeroPointerOut(object? sender, PointerEventArgs e) => HeroRing.IsPaused = false;

    /// <summary>转圈走满一圈 ⇒ 切下一张。<see cref="HeroAdvanceRing.RestartOn"/> 绑着 HeroIndex,
    /// 换页后进度自动归零,不用在这里重开。</summary>
    private void OnHeroRingCompleted(object? sender, EventArgs e)
    {
        if (DataContext is BrowseViewModel vm && vm.NextHeroCommand.CanExecute(null))
            vm.NextHeroCommand.Execute(null);
    }

    /// <summary>货架左右箭头:按视口的 80% 平移,留一点重叠 —— 读者能接上刚才看到的那张卡。
    /// 箭头在区块模板里(拿不到横向 ScrollViewer 的引用),所以向上找**第一个真的包含
    /// ScrollViewer 的祖先**:按 DataContext 找会先撞上按钮那排 StackPanel(它也继承区块 VM)。</summary>
    private void OnShelfScroll(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string direction } button) return;

        var scroller = button.GetVisualAncestors().OfType<Control>()
            .Select(ancestor => ancestor.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault())
            .FirstOrDefault(candidate => candidate is not null);
        if (scroller is null) return;

        var max = Math.Max(0, scroller.Extent.Width - scroller.Viewport.Width);
        var step = scroller.Viewport.Width * 0.8;
        var target = direction == "next" ? scroller.Offset.X + step : scroller.Offset.X - step;
        scroller.Offset = new Vector(Math.Clamp(target, 0, max), scroller.Offset.Y);
    }
}
