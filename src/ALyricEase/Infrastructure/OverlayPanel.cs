using Avalonia;
using Avalonia.Controls;

namespace ALyricEase.Infrastructure;

/// <summary>覆盖层面板:子元素不参与父级测量(测得 0×0),排列时一律铺满面板最终尺寸。
/// 专治"装饰性背景(模糊头像等 UniformToFill 大图)在无约束 Panel 里按图片原始尺寸撑爆容器":
/// 容器高度完全由同级内容决定,背景图只是被拉着铺满,结构上不可能反向影响布局
/// —— 不需要任何 SizeChanged 回写(那种方案有正反馈缺陷:回写的正是被图片撑大的值)。</summary>
public sealed class OverlayPanel : Panel
{
    protected override Size MeasureOverride(Size availableSize) => default;

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var child in Children)
        {
            child.Measure(finalSize);
            child.Arrange(new Rect(finalSize));
        }
        return finalSize;
    }
}
