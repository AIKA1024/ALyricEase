using Avalonia.Controls;
using ALyricEase.Infrastructure;

namespace ALyricEase.Views;

public partial class ArtistView : UserControl
{
    private readonly DetailPageScrollController _scrollController;

    public ArtistView()
    {
        InitializeComponent();
        _scrollController = new DetailPageScrollController(this, PageScroller);
        SizeChanged += (_, e) => ResponsiveClasses.ApplyByWindow(this);
        AttachedToVisualTree += (_, _) => ResponsiveClasses.ApplyByWindow(this);
    }
}
