using ALyricEase.Infrastructure;
using ALyricEase.ViewModels;
using Avalonia.Controls;
using Avalonia.Threading;

namespace ALyricEase.Views;

/// <summary>最近播放页：歌曲行虚拟化，并在滚动停止后批量加载可见封面与红心状态。</summary>
public partial class RecentPlaybackView : UserControl
{
    private DispatcherTimer? _coverDebounce;
    private readonly HashSet<SongItemViewModel> _pendingCovers = new();

    public RecentPlaybackView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => ResponsiveClasses.ApplyByWindow(this);
        AttachedToVisualTree += (_, _) => ResponsiveClasses.ApplyByWindow(this);
    }

    private void OnTrackContainerPreparing(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container?.DataContext is not SongItemViewModel item) return;
        _pendingCovers.Add(item);
        if (_coverDebounce is { IsEnabled: true }) return;
        _coverDebounce ??= new DispatcherTimer(
            TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, OnCoverDebounceTick);
        _coverDebounce.Start();
    }

    private void OnCoverDebounceTick(object? sender, EventArgs e)
    {
        _coverDebounce?.Stop();
        foreach (var item in _pendingCovers) item.EnsureLikedLoaded();
        _pendingCovers.Clear();
    }
}
