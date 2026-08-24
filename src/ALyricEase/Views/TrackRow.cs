using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ALyricEase.ViewModels;
using Visual = Avalonia.Visual;

namespace ALyricEase.Views;

/// <summary>可复用歌曲行:一个控件 + 多套 ControlTheme(仿原版 UWP TrackListItem)。
/// 控件只负责行为(双击播放);每日行歌手/专辑菜单用 MenuFlyout,原生处理子菜单(hover 延迟/定位)。
/// 列布局/播放按钮位置由各 Theme 的模板决定——每日行套 Search 主题(播放按钮靠右),歌单行套 PlaylistWide 主题。
/// 悬停浮现操作按钮、时长隐藏由共享样式按类名驱动,与模板摆放无关。</summary>
public class TrackRow : TemplatedControl
{
    private Button? _artistAlbumButton;
    private Button? _artistButton;

    public TrackRow()
    {
        // 双击整行播放;排除内部按钮(播放/更多)的来源
        AddHandler(InputElement.DoubleTappedEvent, OnRowDoubleTapped, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        // 模板重新应用时先解除旧按钮订阅,避免重复
        if (_artistAlbumButton is { } oldButton)
            oldButton.RemoveHandler(Button.ClickEvent, OnArtistAlbumClick);
        if (_artistButton is { } oldArtist)
            oldArtist.RemoveHandler(Button.ClickEvent, OnArtistButtonClick);

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

    private static void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual source && source.FindAncestorOfType<Button>() is not null) return;
        if (sender is Control { DataContext: SongItemViewModel song })
        {
            song.PlayCommand.Execute(null);
            e.Handled = true;
        }
    }
}
