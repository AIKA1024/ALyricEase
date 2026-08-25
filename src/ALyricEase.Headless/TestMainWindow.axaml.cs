using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace ALyricEase.Headless;

/// <summary>TEMP-DIAG:TestMainWindow.axaml 的代码后置桩(复制自真实 MainWindow,
/// 事件处理器为空实现,仅供无头复现编译型 XAML 的属性应用路径)。</summary>
public partial class TestMainWindow : Window
{
    public TestMainWindow()
    {
        InitializeComponent();
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e) { }
    private void OnMinimizeClick(object? sender, RoutedEventArgs e) { }
    private void OnMaximizeClick(object? sender, RoutedEventArgs e) { }
    private void OnCloseClick(object? sender, RoutedEventArgs e) { }
}
