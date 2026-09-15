using Avalonia.Controls;
using ALyricEase.Infrastructure;

namespace ALyricEase.Views;

public partial class ArtistView : UserControl
{
    public ArtistView()
    {
        InitializeComponent();
        SizeChanged += (_, e) => ResponsiveClasses.ApplyByWindow(this);
        AttachedToVisualTree += (_, _) => ResponsiveClasses.ApplyByWindow(this);
    }
}
