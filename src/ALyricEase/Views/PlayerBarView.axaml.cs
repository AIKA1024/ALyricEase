using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using ALyricEase.Infrastructure;
using ALyricEase.ViewModels;
using Avalonia;

namespace ALyricEase.Views;

/// <summary>底部播放条:尺寸与宽屏一致,narrow 仅精简右侧按钮(隐藏红心/模式/音量/更多,留三键),
/// 响应式类按窗口宽度切换。进度条为共享的 PlayerProgressBar(自带拖动/气泡),本类只负责布局切换与"点空白打开正在播放页"。</summary>
public partial class PlayerBarView : UserControl
{
    public PlayerBarView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => ResponsiveClasses.ApplyByWindow(this);
        SizeChanged += (_, _) => ResponsiveClasses.ApplyByWindow(this);
    }

    /// <summary>Tap 封面/曲目区 → 打开正在播放页。用 Tap(按下抬起才算)而非 PointerPressed:
    /// 按下即触发会跟拖动/滑进度误触;按钮点击冒泡上来时,按来源过滤掉。</summary>
    private void OnTrackAreaTap(object? sender, TappedEventArgs e)
    {
        // Grid 处理触摸/鼠标事件时,播放按钮的 Tap 会冒泡;但歌曲区域(含封面)仍应打开详情。
        if (e.Source is Visual source && source.FindAncestorOfType<Button>() is not null) return;

        // 无当前曲目时点击不打开正在播放页(占位标题不可点击)
        if (DataContext is not PlayerViewModel { CurrentSong: not null }) return;

        // 用 AppShell 而非 Window 找 MainViewModel:安卓端由 Activity 承载,可视树里没有 Window
        if (this.FindAncestorOfType<AppShell>()?.DataContext is MainViewModel { ShowNowPlaying: false } vm)
            vm.OpenNowPlayingCommand.Execute(null);
        e.Handled = true;
    }
}
