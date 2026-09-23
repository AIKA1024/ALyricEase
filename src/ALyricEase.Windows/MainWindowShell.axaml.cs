using Avalonia.Controls;

namespace ALyricEase;

/// <summary>主窗口的重内容壳层(标题栏之外的全部),由 MainWindow 在首帧渲染后延迟挂载。
/// NowPlayingOverlay 暴露给宿主:NowPlayingOverlayController 需要按它建立(见 MainWindow.axaml.cs)。</summary>
public partial class MainWindowShell : UserControl
{
    public MainWindowShell()
    {
        InitializeComponent();
    }
}
