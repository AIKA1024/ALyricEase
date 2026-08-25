using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Xaml.Interactivity;

namespace ALyricEase.Infrastructure.Behaviors;

/// <summary>
/// 按住 Ctrl 滚滚轮 → 本 ScrollViewer 水平滚动(每格约半张卡宽),并拦截事件不再冒泡驱动
/// 外层页面纵向滚动;未按 Ctrl 不处理,滚轮照常冒泡。Offset 越界由 ScrollViewer 自动收敛。
/// 用于纵向禁用、仅横向滚动的区块(个性推荐/歌手页的横向列表)。
/// 用法:<c>&lt;i:Interaction.Behaviors&gt;&lt;behaviors:CtrlHorizontalWheelBehavior /&gt;&lt;/i:Interaction.Behaviors&gt;</c>
/// </summary>
public sealed class CtrlHorizontalWheelBehavior : Behavior<ScrollViewer>
{
    /// <summary>每格滚轮的水平位移(px),约半张卡宽(卡 200 + 列距 12)。</summary>
    private const double WheelStep = 120;

    protected override void OnAttached()
    {
        base.OnAttached();
        // 冒泡阶段实例处理器:纵向禁用的区块里模板 presenter 不消费竖向滚轮,
        // 事件能到达本 ScrollViewer(与原 XAML PointerWheelChanged 特性等价)
        AssociatedObject?.PointerWheelChanged += OnWheel;
    }

    protected override void OnDetaching()
    {
        AssociatedObject?.PointerWheelChanged -= OnWheel;
        base.OnDetaching();
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (AssociatedObject is not { } sv) return;
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        if (e.Delta.Y == 0) return;
        sv.Offset = sv.Offset.WithX(sv.Offset.X - e.Delta.Y * WheelStep);
        e.Handled = true;
    }
}
