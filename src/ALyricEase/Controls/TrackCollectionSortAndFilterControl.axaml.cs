using Avalonia.Controls;

namespace ALyricEase.Controls;

/// <summary>原版歌单排序/筛选栏的 Avalonia 移植；只负责布局，集合投影由页面 ViewModel 维护。</summary>
public partial class TrackCollectionSortAndFilterControl : UserControl
{
    private const double WideLayoutWidth = 700;

    public TrackCollectionSortAndFilterControl()
    {
        InitializeComponent();
        SizeChanged += (_, e) => Classes.Set("wide", e.NewSize.Width >= WideLayoutWidth);
    }
}
