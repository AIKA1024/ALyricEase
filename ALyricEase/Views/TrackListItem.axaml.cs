using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Visual = Avalonia.Visual;
using CommunityToolkit.Mvvm.Input;
using ALyricEase.ViewModels;

namespace ALyricEase.Views;

public partial class TrackListItem : UserControl
{
    public TrackListItem()
    {
        InitializeComponent();
        AddHandler(InputElement.DoubleTappedEvent, OnRowDoubleTapped, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e) => OnRowDoubleTapped(sender, e);

    private static void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual source && source.FindAncestorOfType<Button>() is not null) return;
        if (sender is Control { DataContext: SongItemViewModel song })
        {
            song.PlayCommand.Execute(null);
            e.Handled = true;
        }
    }
}
