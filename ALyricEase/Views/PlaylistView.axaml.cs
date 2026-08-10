using Avalonia.Controls;
using ALyricEase.Infrastructure;
using ALyricEase.ViewModels;

namespace ALyricEase.Views;

/// <summary>歌单详情页：登录卡片 / 选中歌单的头部 + 曲目表。歌单入口由左侧 shell 提供。</summary>
public partial class PlaylistView : UserControl
{
    public PlaylistView()
    {
        InitializeComponent();
        SizeChanged += (_, e) => ResponsiveClasses.Apply(this, e.NewSize.Width);
        AttachedToVisualTree += (_, _) => ResponsiveClasses.Apply(this, Bounds.Width);
    }

    /// <summary>曲目容器 realized 时触发封面懒加载(配合列表虚拟化)。</summary>
    private void OnTrackContainerPreparing(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container?.DataContext is SongItemViewModel item)
            item.EnsureCoverLoaded();
    }
}
