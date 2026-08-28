using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ALyricEase.Headless;

/// <summary>VGridWindow 的交互代码:填充分块数据、行容器计数与滚动验证由 VGridProbe 驱动。</summary>
public partial class VGridWindow : Window
{
    public VGridWindow()
    {
        InitializeComponent();
    }

    /// <summary>行容器 realize 时计数(与 SongGridView 的封面懒加载挂点同位)。</summary>
    public void OnRowPrepared(object? sender, ContainerPreparedEventArgs e) => RowsPrepared++;

    /// <summary>累计 realize 的行容器数(含 recycle 后重 realize)。</summary>
    public int RowsPrepared { get; private set; }
}
