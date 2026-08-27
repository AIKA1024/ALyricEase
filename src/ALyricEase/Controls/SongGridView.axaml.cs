using System.Collections;
using Avalonia;
using Avalonia.Controls;
using ALyricEase.ViewModels;

namespace ALyricEase.Controls;

/// <summary>
/// 横向滚动歌曲网格(圆角容器 + 竖向换行 5 行):
/// 个性推荐"每日歌曲推荐"与歌手页"热门歌曲"共用的容器控件。
/// </summary>
public partial class SongGridView : UserControl
{
    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<SongGridView, IEnumerable?>(nameof(ItemsSource));

    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public SongGridView()
    {
        InitializeComponent();
        Rows.Bind(ItemsControl.ItemsSourceProperty, this.GetObservable(ItemsSourceProperty));
    }

    /// <summary>行容器 realized 时加载封面/红心(幂等),原先分散在两个页面的处理统一到这里。</summary>
    private void OnRowContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container?.DataContext is SongItemViewModel song)
        {
            song.EnsureCoverLoaded();
            song.EnsureLikedLoaded();
        }
    }
}
