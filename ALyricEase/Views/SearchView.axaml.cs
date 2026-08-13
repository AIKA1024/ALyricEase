using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using ALyricEase.Infrastructure;
using Avalonia.Input;
using ALyricEase.ViewModels;

namespace ALyricEase.Views;

public partial class SearchView : UserControl
{
    public SearchView()
    {
        InitializeComponent();
        SizeChanged += (_, e) => ResponsiveClasses.Apply(this, e.NewSize.Width);
        AttachedToVisualTree += (_, _) => ResponsiveClasses.Apply(this, Bounds.Width);
    }

    private void OnResultContainerPreparing(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container?.DataContext is SongItemViewModel item)
            item.EnsureCoverLoaded();
    }

    private void OnListDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is SearchViewModel vm && vm.SelectedItem is { } item)
            item.PlayCommand.Execute(null);
    }
}
