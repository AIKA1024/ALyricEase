using ALyricEase.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace ALyricEase.Views;

public partial class DesktopLyricSettingsDialogView : UserControl
{
    private IInputElement? _previousFocus;

    public DesktopLyricSettingsDialogView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != IsVisibleProperty) return;
        if (IsVisible)
        {
            _previousFocus = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
            Dispatcher.UIThread.Post(() => { if (IsVisible) DoneButton.Focus(); });
        }
        else
        {
            _previousFocus?.Focus();
            _previousFocus = null;
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DataContext is not MainViewModel model) return;
        model.CloseDesktopLyricDialogCommand.Execute(null);
        e.Handled = true;
    }
}
