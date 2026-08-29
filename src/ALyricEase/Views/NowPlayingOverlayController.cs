using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Media.Transformation;
using ALyricEase.ViewModels;

namespace ALyricEase.Views;

/// <summary>正在播放覆盖层的滑入/滑出驱动。覆盖层是宿主网格级 Grid,在桌面端与 Android 端
/// 都横跨两行(分别盖住窗口标题栏 / 顶部横幅),常驻可视树,
/// 靠 RenderTransform 在"原位/屏幕外"间切换(0.4s 过渡声明在宿主 XAML 上)。
/// 关闭态"屏幕外"位移按覆盖层自身 Bounds.Height 计算,宿主在尺寸变化时调 UpdateClosedPosition
/// —— 所以覆盖范围改了(加一行/减一行)不用动这里,位移自动跟上。</summary>
public sealed class NowPlayingOverlayController : IDisposable
{
    private const double s_offScreenGuard = 100000;
    private const double s_slideMargin = 8;

    private readonly Grid _overlay;
    private readonly MainViewModel _vm;

    public NowPlayingOverlayController(Grid overlay, MainViewModel vm)
    {
        _overlay = overlay;
        _vm = vm;
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        // 启动即隐藏:attach 前无过渡摆到屏幕外(避免首帧闪现)
        SetOverlayTransformNoTransition(TranslateY(ClosedY));
    }

    /// <summary>布局就绪/尺寸变化后调用:关闭态把屏幕外位置从兜底值更新为真实高度(仍无过渡,不可见)。</summary>
    public void UpdateClosedPosition()
    {
        if (!_vm.ShowNowPlaying)
            SetOverlayTransformNoTransition(TranslateY(ClosedY));
    }

    public void Dispose() => _vm.PropertyChanged -= OnViewModelPropertyChanged;

    private double ClosedY
    {
        get
        {
            var h = _overlay.Bounds.Height;
            return (h > 0 ? h : s_offScreenGuard) + s_slideMargin;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ShowNowPlaying))
            _overlay.RenderTransform = TranslateY(_vm.ShowNowPlaying ? 0 : ClosedY);
    }

    private void SetOverlayTransformNoTransition(TransformOperations transform)
    {
        var transitions = _overlay.Transitions;
        _overlay.Transitions = null;
        _overlay.RenderTransform = transform;
        _overlay.Transitions = transitions;
    }

    private static TransformOperations TranslateY(double y)
    {
        var builder = TransformOperations.CreateBuilder(1);
        builder.AppendTranslate(0, y);
        return builder.Build();
    }
}
