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
        AttachedToVisualTree += (_, _) => RefreshAudioDevices();
        DataContextChanged += (_, _) => RefreshAudioDevices();
    }

    private void RefreshAudioDevices()
    {
        if (DataContext is SettingsViewModel vm) _ = vm.RefreshAudioDevicesCommand.ExecuteAsync(null);
    }
}
