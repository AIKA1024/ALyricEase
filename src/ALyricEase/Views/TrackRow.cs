using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ALyricEase.Infrastructure;
using ALyricEase.ViewModels;
using Visual = Avalonia.Visual;

namespace ALyricEase.Views;

/// <summary>可复用歌曲行:一个控件 + 多套 ControlTheme(仿原版 UWP TrackListItem)。
/// 控件只负责行为(桌面双击播放;触控单击播放,由 InteractionDefaults 按 Head 声明区分);每日行歌手/专辑菜单用 MenuFlyout,原生处理子菜单(hover 延迟/定位)。
/// 列布局/播放按钮位置由各 Theme 的模板决定——每日行套 Search 主题(播放按钮靠右),歌单行套 PlaylistWide 主题。
/// 悬停浮现操作按钮、时长隐藏由共享样式按类名驱动,与模板摆放无关。</summary>
public class TrackRow : TemplatedControl
{
    private Button? _artistAlbumButton;
    private Button? _artistButton;
    private Button? _moreButton;

    public TrackRow()
    {
        // 桌面:双击整行播放;触控(InteractionDefaults 由各 Head 启动时声明):单击整行播放。
        // 触控模式只订阅 Tapped——快速双击会连发两次 Tapped,DoubleTapped 再订阅会放大重复触发,
        // 且触控本就无"双击播放"语义。两种事件共用同一处理,排除内部按钮(播放/更多/红心/歌手)的来源。
        if (InteractionDefaults.IsTouchPrimary)
        {
            // 挂 touch 类:让 TrackRow.axaml 末尾的 touch 覆盖样式驱动"无 hover"差异(按钮常显/时长隐藏)
            Classes.Add(InteractionDefaults.TouchClass);
            AddHandler(InputElement.TappedEvent, OnRowActivated, RoutingStrategies.Bubble, handledEventsToo: true);
        }
        else
        {
            AddHandler(InputElement.DoubleTappedEvent, OnRowActivated, RoutingStrategies.Bubble, handledEventsToo: true);
        }

        // 右键(桌面)/长按(触控)统一走 ContextRequested:弹歌曲菜单(与播放条同一份)
        AddHandler(InputElement.ContextRequestedEvent, OnRowContextRequested, RoutingStrategies.Bubble);
    }

    /// <summary>整行右键(桌面)/长按(触控)→ 弹歌曲菜单(与播放条同一份,构造见 SongContextMenu)。
    /// 桌面右键在"松开"时触发,且按下期间指针被隐式捕获到行上:按住拖出行外再松开也会路由到这里,
    /// 但语义应同按钮点击("按下+抬起都在其上才算"),松开点已不在行内就不弹。键盘 Menu 键
    /// 请求时 TryGetPosition 返回 false,照常弹出。更多按钮点击则贴按钮展开(见 OnMoreButtonClick)。</summary>
    private void OnRowContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (DataContext is not SongItemViewModel song) return;
        if (e.TryGetPosition(this, out var pt)
            && (pt.X < 0 || pt.Y < 0 || pt.X > Bounds.Width || pt.Y > Bounds.Height))
            return;
        SongContextMenu.Create(this, song.Song, song.SourceName, row: song).ShowAt(this, true);
        e.Handled = true;
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        // 模板重新应用时先解除旧按钮订阅,避免重复
        if (_artistAlbumButton is { } oldButton)
            oldButton.RemoveHandler(Button.ClickEvent, OnArtistAlbumClick);
        if (_artistButton is { } oldArtist)
            oldArtist.RemoveHandler(Button.ClickEvent, OnArtistButtonClick);
        if (_moreButton is { } oldMore)
            oldMore.RemoveHandler(Button.ClickEvent, OnMoreButtonClick);

        base.OnApplyTemplate(e);

        // 每日行歌手/专辑菜单:点击(按下并抬起)才展开,按下不触发(Search 主题才有;宽主题无此部件,Find 返回 null 即跳过)
        if (e.NameScope.Find("ArtistAlbumButton") is Button button)
        {
            _artistAlbumButton = button;
            button.AddHandler(Button.ClickEvent, OnArtistAlbumClick);
        }
        else
        {
            _artistAlbumButton = null;
        }

        // 歌单行歌手按钮:多歌手点击弹菜单选歌手;单歌手不拦截,让 Command 直接跳(PlaylistWide 主题才有)
        if (e.NameScope.Find("ArtistButton") is Button artistButton)
        {
            _artistButton = artistButton;
            artistButton.AddHandler(Button.ClickEvent, OnArtistButtonClick);
        }
        else
        {
            _artistButton = null;
        }

        // "更多"(三个点)按钮:三个主题都有,点击弹歌曲菜单(与右键/长按同一份);无此部件即跳过
        if (e.NameScope.Find("MoreButton") is Button moreButton)
        {
            _moreButton = moreButton;
            moreButton.AddHandler(Button.ClickEvent, OnMoreButtonClick);
        }
        else
        {
            _moreButton = null;
        }
    }

    /// <summary>点击(抬起)歌手/专辑按钮 → 展开按钮下方的 MenuFlyout。
    /// Click 无指针坐标,不再定位到按下点,统一贴按钮展开。</summary>
    private void OnArtistAlbumClick(object? sender, RoutedEventArgs e)
    {
        if (_artistAlbumButton is not { } button) return;
        if (button.Resources["TrackMenuFlyout"] is not MenuFlyout flyout) return;
        flyout.ShowAt(button);
        e.Handled = true;
    }

    /// <summary>歌单行歌手按钮:仅多歌手时弹菜单选歌手;单歌手走 Command 直接跳。
    /// Click 标记 Handled 后 Button 不再执行 Command,天然挡住"跳第一个歌手"。</summary>
    private void OnArtistButtonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { } button) return;
        if (button.DataContext is not SongItemViewModel { HasMultipleArtists: true }) return; // 单歌手:走 Command
        if (button.Resources["ArtistMenuFlyout"] is not MenuFlyout flyout) return;
        flyout.ShowAt(button);
        e.Handled = true;
    }

    /// <summary>点击(抬起)"更多"按钮 → 贴按钮展开歌曲菜单。Click 无指针坐标,统一贴按钮展开。</summary>
    private void OnMoreButtonClick(object? sender, RoutedEventArgs e)
    {
        if (_moreButton is not { } button) return;
        if (DataContext is not SongItemViewModel song) return;
        SongContextMenu.Create(this, song.Song, song.SourceName, row: song).ShowAt(button);
        e.Handled = true;
    }

    /// <summary>行激活播放:指针模式由双击触发,触控模式由单击触发(见构造函数订阅)。
    /// 排除来自内部按钮的命中,避免整行行为劫持按钮点击。</summary>
    private static void OnRowActivated(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual source && source.FindAncestorOfType<Button>() is not null) return;
        if (sender is Control { DataContext: SongItemViewModel song })
        {
            song.PlayCommand.Execute(null);
            e.Handled = true;
        }
    }
}
