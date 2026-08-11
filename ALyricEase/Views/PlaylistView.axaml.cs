using Avalonia.Controls;
using Avalonia.Threading;
using ALyricEase.Infrastructure;
using ALyricEase.ViewModels;

namespace ALyricEase.Views;

/// <summary>歌单详情页：登录卡片 / 选中歌单的头部 + 曲目表。歌单入口由左侧 shell 提供。</summary>
public partial class PlaylistView : UserControl
{
    private DispatcherTimer? _coverDebounce;
    private readonly System.Collections.Generic.HashSet<SongItemViewModel> _pendingCovers = new();

    public PlaylistView()
    {
        InitializeComponent();
        SizeChanged += (_, e) => ResponsiveClasses.Apply(this, e.NewSize.Width);
        AttachedToVisualTree += (_, _) => ResponsiveClasses.Apply(this, Bounds.Width);
    }

    /// <summary>曲目容器 realized 时触发封面懒加载。封面节流:滚动中不立即加载,
    /// 停止 150ms 后统一加载可见行,减少滚动时封面解码/Image 更新造成的渲染与内存波动。</summary>
    private void OnTrackContainerPreparing(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container?.DataContext is not SongItemViewModel item) return;
        _coverDebounce ??= new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, OnCoverDebounceTick);
        _coverDebounce.Stop();
        _pendingCovers.Add(item);
        _coverDebounce.Start();
    }

    private void OnCoverDebounceTick(object? sender, EventArgs e)
    {
        _coverDebounce?.Stop();
        foreach (var item in _pendingCovers) item.EnsureCoverLoaded();
        _pendingCovers.Clear();
    }
}
