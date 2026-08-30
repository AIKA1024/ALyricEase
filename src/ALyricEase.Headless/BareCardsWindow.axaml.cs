using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Linq;

namespace ALyricEase.Headless;

/// <summary>BareCardsWindow 的交互代码:数据填充与状态读取由 ArtistCardsProbe 驱动。</summary>
public partial class BareCardsWindow : Window
{
    public BareCardsWindow()
    {
        InitializeComponent();
    }

    public ScrollViewer Scroll => this.GetVisualDescendants().OfType<ScrollViewer>().First();

    public VirtualizingStackPanel Panel => this.GetVisualDescendants().OfType<VirtualizingStackPanel>().First();

    public void Drain()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
    }
}
