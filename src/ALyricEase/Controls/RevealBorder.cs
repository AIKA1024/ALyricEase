using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ALyricEase.Controls;

/// <summary>
/// 弹出层"从选中项向两侧展开"的载体（WinUI ComboBox 的 SplitOpen 观感）。
///
/// 为什么不是 ScaleTransform：WinUI 的展开是**裁剪揭示**，内容不缩放（文字不会被压扁）。
/// 所以这里直接改 <see cref="Visual.Clip"/>：以 <see cref="RevealAnchorY"/> 为中心，
/// 上下两条边同时向外推到 0 / 全高。<see cref="RevealProgress"/>=1 时把 Clip 清成 null，
/// 避免长期挂着一个几何体每帧参与裁剪。
///
/// 注意 Clip 只作用于本控件与子级，弹层的阴影画在**另一个** Border 上，不受影响。
/// </summary>
public class RevealBorder : Border
{
    public static readonly StyledProperty<double> RevealProgressProperty =
        AvaloniaProperty.Register<RevealBorder, double>(nameof(RevealProgress), 1.0);

    public static readonly StyledProperty<double> RevealAnchorYProperty =
        AvaloniaProperty.Register<RevealBorder, double>(nameof(RevealAnchorY), double.NaN);

    /// <summary>0 = 完全收起（不可见），1 = 完整显示。</summary>
    public double RevealProgress
    {
        get => GetValue(RevealProgressProperty);
        set => SetValue(RevealProgressProperty, value);
    }

    /// <summary>展开原点（相对本控件左上角，Y 方向）。NaN 表示按垂直居中。</summary>
    public double RevealAnchorY
    {
        get => GetValue(RevealAnchorYProperty);
        set => SetValue(RevealAnchorYProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == RevealProgressProperty ||
            change.Property == RevealAnchorYProperty ||
            change.Property == BoundsProperty)
        {
            UpdateClip();
        }
    }

    private void UpdateClip()
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        var progress = Math.Clamp(RevealProgress, 0, 1);

        if (progress >= 1 || width <= 0 || height <= 0)
        {
            Clip = null;
            return;
        }

        var anchor = double.IsNaN(RevealAnchorY) ? height / 2 : Math.Clamp(RevealAnchorY, 0, height);
        var top = anchor * (1 - progress);
        var bottom = anchor + (height - anchor) * progress;

        Clip = new RectangleGeometry(new Rect(0, top, width, Math.Max(0, bottom - top)));
    }
}
