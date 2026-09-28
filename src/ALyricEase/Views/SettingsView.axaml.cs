using System;
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

    /// <summary>淡化时长滑杆宽 = min(280, 行宽 - 图标/标题/时长文本实际宽度)。
    /// 写死 280 窄屏溢出卡片;Stretch+MaxWidth 会被居中(实测,宽屏不靠右)。
    /// 行宽变化驱动,滑杆宽度不反推行宽,无布局反馈回路。子元素 Bounds 在行 arrange 时
    /// 已同步更新,首帧即正确。</summary>
    private void OnCrossfadeRowSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        var fixedParts = CrossfadeIcon.Bounds.Width + CrossfadeTitle.Bounds.Width
            + CrossfadeSecondsText.Bounds.Width + CrossfadeSecondsText.Margin.Left
            + CrossfadeSecondsText.Margin.Right;
        CrossfadeSlider.Width = Math.Clamp(e.NewSize.Width - fixedParts, 0, 280);
    }
}
