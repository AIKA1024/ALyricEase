using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using ALyricEase.ViewModels;

namespace ALyricEase.Views;

public partial class TrackListItem : UserControl
{
    /// <summary>窄窗阈值:低于此隐藏专辑 + 时长列。</summary>
    private const double WideThreshold = 460;

    public TrackListItem()
    {
        InitializeComponent();
        SizeChanged += OnSizeChanged;
        Loaded += (_, _) => ApplyLayout(Bounds.Width);
        // 悬停:显示更多按钮并淡出时长(自身类型选择器无法在自身文件内解析,用代码切换)
        PointerEntered += OnRowPointerEntered;
        PointerExited += OnRowPointerExited;
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e) => ApplyLayout(e.NewSize.Width);

    /// <summary>Wide 显示全部四列;Narrow 收起专辑/时长列(与歌单列表头同步)。</summary>
    private void ApplyLayout(double width)
    {
        var narrow = width > 0 && width < WideThreshold;
        // Avalonia 命名 ColumnDefinition 不会生成字段 → 经 Grid.ColumnDefinitions 访问
        RootGrid.ColumnDefinitions[2].Width = new GridLength(narrow ? 0 : 180);
        RootGrid.ColumnDefinitions[3].Width = new GridLength(narrow ? 0 : 100);
        AlbumText.IsVisible = !narrow;
        DurText.IsVisible = !narrow;
    }

    private void OnRowPointerEntered(object? sender, PointerEventArgs e)
    {
        MoreButton.IsVisible = true;
        DurText.Opacity = 0;
    }

    private void OnRowPointerExited(object? sender, PointerEventArgs e)
    {
        MoreButton.IsVisible = false;
        DurText.Opacity = 1;
    }

    /// <summary>双击整行播放。</summary>
    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is SongItemViewModel song)
            song.PlayCommand.Execute(null);
    }
}
