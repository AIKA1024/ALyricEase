using ALyricEase.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace ALyricEase.Views;

public partial class ErrorDialogView : UserControl
{
    public ErrorDialogView()
    {
        InitializeComponent();
        PropertyChanged += (_, e) =>
        {
            if (e.Property != IsVisibleProperty || !IsVisible) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (IsVisible && IsEnabled) ConfirmButton.Focus();
            }, DispatcherPriority.Loaded);
        };
        KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Escape or Key.Enter) || DataContext is not MainViewModel main) return;
            main.CloseErrorDialogCommand.Execute(null);
            e.Handled = true;
        };
    }
}
