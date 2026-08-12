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

    public TrackRow()
    {
        // 双击整行播放;排除内部按钮(播放/更多)的来源
        AddHandler(InputElement.DoubleTappedEvent, OnRowDoubleTapped, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        // 模板重新应用时先解除旧按钮订阅,避免重复
        if (_artistAlbumButton is { } oldButton)
            oldButton.RemoveHandler(InputElement.PointerPressedEvent, OnArtistAlbumPointerPressed);

        base.OnApplyTemplate(e);

        // 每日行歌手/专辑菜单:在鼠标点击位置展开(Search 主题才有;宽主题无此部件,Find 返回 null 即跳过)
        if (e.NameScope.Find("ArtistAlbumButton") is Button button)
        {
            _artistAlbumButton = button;
            // Tunnel + handledEventsToo:Button 自身会处理 Pointer/Click,普通 += 可能不触发
            button.AddHandler(InputElement.PointerPressedEvent, OnArtistAlbumPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        }
        else
        {
            _artistAlbumButton = null;
        }
    }

    /// <summary>点击歌手/专辑按钮 → 在指针位置展开 MenuFlyout(替代 Button.Flyout 默认底部定位)。</summary>
    private void OnArtistAlbumPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
        if (_artistAlbumButton is not { } button) return;
        if (button.Resources["TrackMenuFlyout"] is not MenuFlyout flyout) return;
        flyout.ShowAt(button, true); // showAtPointer:在鼠标点击位置展开
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
