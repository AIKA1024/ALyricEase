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
        if (sender is Grid row)
            row.Classes.Set("compact", e.NewSize.Width < 360);
    }

    /// <summary>淡化时长滑杆宽 = min(280, 行宽 - 图标/标题/时长文本实际占用)。
    /// 写死 280 窄屏溢出卡片;Stretch+MaxWidth 会被居中(实测,宽屏不靠右)。
    /// 行宽变化驱动,滑杆宽度不反推行宽,无布局反馈回路。子元素 Bounds 在行 arrange 时
    /// 已同步更新,首帧即正确。
    /// ⚠ 必须把每个元素的水平 Margin 一并计入(setting-icon 样式自带右侧 16px):
    /// 漏算会让滑杆比 * 列宽同样多、右对齐后向左溢出,恰好盖掉文本右侧的间隙,
    /// 观感"文本和滑杆贴在一起"且改文本 Margin 几乎无效(被溢出吃掉)。</summary>
    private void OnCrossfadeRowSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        static double Hrz(TextBlock el) => el.Bounds.Width + el.Margin.Left + el.Margin.Right;
        var fixedParts = Hrz(CrossfadeIcon) + Hrz(CrossfadeTitle) + Hrz(CrossfadeSecondsText);
        CrossfadeSlider.Width = Math.Clamp(e.NewSize.Width - fixedParts, 0, 280);
    }
}
