using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace ALyricEase.Controls;

public sealed class ChromeAcrylicLayer : Panel
{
    private static RenderTargetBitmap? s_hostMask;
    private readonly Image _hostReveal = new() { Stretch = Stretch.Fill, IsVisible = false };
    private TopLevel? _host;
    private ExperimentalAcrylicBorder? _chrome;

    public ChromeAcrylicLayer()
    {
        IsHitTestVisible = false;
        RenderOptions.SetBitmapBlendingMode(_hostReveal, BitmapBlendingMode.DestinationOut);
        Children.Add(_hostReveal);
        ActualThemeVariantChanged += (_, _) => UpdateMaterial();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        base.MeasureOverride(availableSize);
        // 遮罩图像要铺满已分配的空间，弹窗的期望尺寸只由菜单项决定。
        return default;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _host = TopLevel.GetTopLevel(this);
        if (_host is null) return;
        UpdateMaterial();
        _host.PropertyChanged += OnHostPropertyChanged;
        UpdateHostReveal();
    }

    private void UpdateMaterial()
    {
        if (_host is null || !this.TryFindResource("AppAcrylicMaterial", ActualThemeVariant, out var value)
            || value is not ExperimentalAcrylicMaterial material || _chrome?.Material == material) return;

        // 保持每个亚克力控件的材质引用稳定：弹层脱树时动态资源会短暂变为 null。
        if (_chrome is not null) Children.Remove(_chrome);
        _chrome = new ExperimentalAcrylicBorder
        {
            Material = material,
            // Android 的材质底层完全不透明，通过遮罩让下方已模糊的页面参与混色。
            OpacityMask = new SolidColorBrush(Colors.White, OperatingSystem.IsAndroid() ? 0.65 : 230 / 255d),
        };
        Children.Add(_chrome);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_host is not null) _host.PropertyChanged -= OnHostPropertyChanged;
        _host = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnHostPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TopLevel.ActualTransparencyLevelProperty) UpdateHostReveal();
    }

    private void UpdateHostReveal()
    {
        var level = _host?.ActualTransparencyLevel;
        _hostReveal.IsVisible = level == WindowTransparencyLevel.AcrylicBlur || level == WindowTransparencyLevel.Blur;
        if (_hostReveal.IsVisible) _hostReveal.Source = s_hostMask ??= CreateHostMask();
    }

    // 页面是不透明的。在支持系统模糊的窗口里先透出窗口背板，再叠标题栏材质。
    // 遮罩只绘制一次；剩余的页面像素由外层 BackdropEffect 实时模糊。
    private static RenderTargetBitmap CreateHostMask()
    {
        var bitmap = new RenderTargetBitmap(new PixelSize(1, 1), new Vector(96, 96));
        using var context = bitmap.CreateDrawingContext();
        context.FillRectangle(new SolidColorBrush(Color.FromArgb(224, 255, 255, 255)), new Rect(0, 0, 1, 1));
        return bitmap;
    }
}
