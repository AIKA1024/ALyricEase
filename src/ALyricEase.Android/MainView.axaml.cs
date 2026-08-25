using Avalonia.Controls;
using ALyricEase.ViewModels;
using ALyricEase.Views;

namespace ALyricEase;

public partial class MainView : UserControl
{
    private NowPlayingOverlayController? _nowPlayingController;

    public MainView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => _nowPlayingController?.UpdateClosedPosition();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _nowPlayingController?.Dispose();
        _nowPlayingController = DataContext is MainViewModel vm
            ? new NowPlayingOverlayController(NowPlayingOverlay, vm)
            : null;
    }
}
