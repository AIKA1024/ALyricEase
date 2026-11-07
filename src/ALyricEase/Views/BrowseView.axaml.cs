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

    /// <summary>歌单/榜单卡封面上的 ▶:播放整张歌单(榜单走 TopId 分支)。Handled 阻止 Click
    /// 继续冒泡,避免顺带触发卡片的打开命令;歌曲卡没有这个圆钮(点击即播放)。</summary>
    private void OnCardPlayClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { DataContext: BrowseCardViewModel { Playlist: { } playlist } })
            ServiceLocator.Get<PlayerViewModel>().PlayPlaylistCommand.Execute(playlist);
    }

    /// <summary>歌单/榜单卡封面上的 •••:歌单 more 菜单(播放/收藏/创建者/分享/复制链接),贴按钮弹出。</summary>
    private void OnCardMoreClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { DataContext: BrowseCardViewModel { Playlist: { } playlist } } button)
            PlaylistContextMenu.Create(button, playlist).ShowAt(button);
    }

    /// <summary>歌单/榜单卡右键(桌面)或长按(触摸):与 ••• 同一份即时构造的菜单,贴指针弹出。
    /// 歌曲卡没有歌单数据,不弹菜单。</summary>
    private void OnCardContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Button { DataContext: BrowseCardViewModel { Playlist: { } playlist } } button) return;
        e.Handled = true;
        PlaylistContextMenu.Create(button, playlist).ShowAt(button, true);
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
