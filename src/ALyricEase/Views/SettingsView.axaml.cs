using ALyricEase.ViewModels;
using Avalonia.Controls;

namespace ALyricEase.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        // 设置页由 AppShell 的 DataTemplate 每次进入重建 ⇒ 在这里重扫一次输出设备,
        // 让刚插上的耳机/USB 声卡立刻出现在下拉里(不用重开应用)。
        AttachedToVisualTree += (_, _) => RefreshPlatformSettings();
        DataContextChanged += (_, _) => RefreshPlatformSettings();
    }

    private void RefreshPlatformSettings()
    {
        if (DataContext is not SettingsViewModel vm) return;
        vm.RefreshDesktopSettings();
        _ = vm.RefreshAudioDevicesCommand.ExecuteAsync(null);
    }

    private void OnCacheLimitRowSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is not Grid row) return;
        row.Classes.Set("compact", e.NewSize.Width < 360);
        row.Classes.Set("minimal", e.NewSize.Width < 232);
    }
}
