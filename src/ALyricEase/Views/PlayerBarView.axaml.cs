using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace ALyricEase.Views;

/// <summary>底部播放条:尺寸与宽屏一致,narrow 仅精简右侧按钮(隐藏红心/模式/音量/更多,留三键),
/// 响应式类按窗口宽度切换。进度条为共享的 PlayerProgressBar(自带拖动/气泡)；
/// 点空白打开正在播放页，桌面右键/触控长按/右侧更多按钮弹出当前歌曲菜单
/// (菜单构造见共享的 SongContextMenu,TrackRow 的右键/长按/更多按钮也弹同一份)。</summary>
public partial class PlayerBarView : UserControl
{
    public PlayerBarView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => ResponsiveClasses.ApplyByWindow(this);
        SizeChanged += (_, _) => ResponsiveClasses.ApplyByWindow(this);
        MoreButton.Click += OnMoreButtonClick;
    }

    /// <summary>Tap 封面/曲目区 → 打开正在播放页。用 Tap(按下抬起才算)而非 PointerPressed:
    /// 按下即触发会跟拖动/滑进度误触;按钮点击冒泡上来时,按来源过滤掉。</summary>
    private void OnTrackAreaTap(object? sender, TappedEventArgs e)
    {
        // Grid 处理触摸/鼠标事件时,播放按钮的 Tap 会冒泡;但歌曲区域(含封面)仍应打开详情。
        if (e.Source is Visual source && source.FindAncestorOfType<Button>() is not null) return;

        // 无当前曲目时点击不打开正在播放页(占位标题不可点击)
        if (DataContext is not PlayerViewModel { CurrentSong: not null }) return;

        // 用 AppShell 而非 Window 找 MainViewModel:移动端由 Activity/ViewController 承载,可视树里没有 Window
        if (this.FindAncestorOfType<AppShell>()?.DataContext is MainViewModel { ShowNowPlaying: false } vm)
            vm.OpenNowPlayingCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>ContextRequested 在桌面由右键触发，在触控平台由长按触发。
    /// 菜单按当前歌曲即时构造，避免切歌后静态 Flyout 仍持有上一首歌的命令参数。
    /// 松开点不在播放条内(按住拖出去再松开,指针捕获仍会路由回来)则不弹,语义同按钮点击。</summary>
    private void OnPlayerContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (DataContext is not PlayerViewModel { CurrentSong: { } song } player) return;
        if (e.TryGetPosition(PlayerRoot, out var pt)
            && (pt.X < 0 || pt.Y < 0 || pt.X > PlayerRoot.Bounds.Width || pt.Y > PlayerRoot.Bounds.Height))
            return;

        var menu = CreateSongMenu(song, player.QueueSourceName);
        menu.ShowAt(PlayerRoot, true);
        e.Handled = true;
    }

    internal MenuFlyout CreateSongMenu(Song song, string? queueSourceName)
        => SongContextMenu.Create(PlayerRoot, song, queueSourceName);

    /// <summary>更多按钮(中屏/大屏显示)点击:贴按钮弹出当前歌曲菜单。无当前曲目时不弹。</summary>
    private void OnMoreButtonClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not PlayerViewModel { CurrentSong: { } song } player) return;
        CreateSongMenu(song, player.QueueSourceName).ShowAt(MoreButton);
        e.Handled = true;
    }
}
