using ALyricEase.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ALyricEase.Views;

public partial class DownloadSongDialogView : UserControl
{
    private TopLevel? _topLevel;
    private InputElement? _previousFocus;

    public DownloadSongDialogView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) =>
        {
            _topLevel = TopLevel.GetTopLevel(this);
            _topLevel?.AddHandler(InputElement.KeyDownEvent, OnModalKeyDown, RoutingStrategies.Tunnel);
            _topLevel?.AddHandler(InputElement.GotFocusEvent, OnModalGotFocus, RoutingStrategies.Bubble);
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _topLevel?.RemoveHandler(InputElement.KeyDownEvent, OnModalKeyDown);
            _topLevel?.RemoveHandler(InputElement.GotFocusEvent, OnModalGotFocus);
            _topLevel = null;
        };
        PropertyChanged += (_, e) =>
        {
            if (e.Property != IsVisibleProperty) return;
            if (IsVisible)
            {
                _previousFocus = _topLevel?.FocusManager?.GetFocusedElement() as InputElement;
                Dispatcher.UIThread.Post(FocusChoice, DispatcherPriority.Loaded);
            }
            else if (_previousFocus is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true })
                _previousFocus.Focus();
        };
    }

    private void FocusChoice()
    {
        if (!IsVisible || !IsEnabled) return;
        if (QualityList.IsEnabled && QualityList.ContainerFromIndex(QualityList.SelectedIndex)
            is InputElement item && item.Focus()) return;
        CloseButton.Focus();
    }

    private void OnModalKeyDown(object? sender, KeyEventArgs e)
    {
        if (!IsVisible || (DataContext as MainViewModel)?.IsErrorDialogOpen == true) return;
        if (e.Key == Key.Escape)
        {
            (DataContext as MainViewModel)?.DownloadSongDialog?.CloseCommand.Execute(null);
            e.Handled = true;
        }
        else if (!IsEnabled || e.Source is not Visual source || !this.IsVisualAncestorOf(source))
        {
            e.Handled = true;
            FocusChoice();
        }
    }

    private void OnModalGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (IsVisible && IsEnabled && (DataContext as MainViewModel)?.IsErrorDialogOpen != true
            && e.Source is Visual source && !this.IsVisualAncestorOf(source)) FocusChoice();
    }
}
