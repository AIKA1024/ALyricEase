using System.Collections;
using Avalonia;
using Avalonia.Controls;

namespace ALyricEase.Controls;

/// <summary>全部专辑详细列表：复用歌曲行视觉和托管封面，保留纵向虚拟化。</summary>
public partial class AlbumList : UserControl
{
    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<AlbumList, IEnumerable?>(nameof(ItemsSource));

    public AlbumList()
    {
        InitializeComponent();
        Rows.Bind(ItemsControl.ItemsSourceProperty, this.GetObservable(ItemsSourceProperty));
        SizeChanged += (_, e) => Classes.Set("compact-metadata", e.NewSize.Width < 500);
    }

    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }
}
