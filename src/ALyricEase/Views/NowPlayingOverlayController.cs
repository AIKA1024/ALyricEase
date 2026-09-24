using System;
using System.ComponentModel;
using System.Numerics;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Rendering.Composition;
using Avalonia.VisualTree;
using ALyricEase.ViewModels;

namespace ALyricEase.Views;

/// <summary>正在播放覆盖层的滑入/滑出驱动。覆盖层是宿主网格级 Grid,在桌面端与 Android 端
/// 都横跨两行(分别盖住窗口标题栏 / 顶部横幅),常驻可视树,
/// 靠【合成线程】的 Translation 动画在"原位/屏幕外"间滑入滑出(0.45s,同原 XAML 过渡曲线)。
/// 不用 UI 线程过渡的原因:第一次打开时 UI 线程正被详情页首实化(大封面解码/合成视觉建立)塞满,
/// UI 线程过渡必然掉帧;合成动画独立推进,全程平滑(与壳层上滑同案,见 MainWindow.AttachShell)。
/// 用 CompositionVisual.Translation 而非 Offset:布局拥有 Offset,会与我们的摆位互相覆盖;
/// Translation 是 Avalonia 的附加偏移,布局不碰,摆位/动画/尺寸修正互不干扰。
/// 关闭态"屏幕外"位移按覆盖层自身 Bounds.Height 计算,宿主在尺寸变化时调 UpdateClosedPosition
/// —— 所以覆盖范围改了(加一行/减一行)不用动这里,位移自动跟上。</summary>
public sealed class NowPlayingOverlayController : IDisposable
{
    private const double s_offScreenGuard = 100000;
    private const double s_slideMargin = 8;
    private static readonly TimeSpan s_slideDuration = TimeSpan.FromMilliseconds(450);

    // 进入与收回均先慢后快;末段斜率收敛,避免标准 ease-in 在最后突然冲刺(原 XAML 过渡同款曲线)
    private static readonly SplineEasing s_slideEasing = new(0.45, 0.08, 0.7, 0.85);

    private readonly Grid _overlay;
    private readonly MainViewModel _vm;
    private CompositionVisual? _visual;
    // 最近一次逻辑位置(开/关/钉住都会更新)。合成动画结束后属性 getter 不反映末帧,
    // 起点必须从这里取,否则关闭动画会退化为零位移瞬跳。
    private Vector3 _current;

    public NowPlayingOverlayController(Grid overlay, MainViewModel vm)
    {
        _overlay = overlay;
        _vm = vm;
        _vm.PropertyChanged += OnViewModelPropertyChanged;

        // ⚠ 控制器在 ShellHost.Content 刚赋值、首次布局之前创建 —— 覆盖层此刻还没进合成树,
        // GetElementVisual 可能拿不到视觉(或挂载时视觉被重建):直接 PinClosed 会空转,
        // 覆盖层就以打开姿态可见且无法关闭。挂到合成树后再取视觉并补闭位。
        if (TryInitVisual())
            PinClosed();
        else
            _overlay.AttachedToVisualTree += OnOverlayAttachedForInit;
    }

    private void OnOverlayAttachedForInit(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _overlay.AttachedToVisualTree -= OnOverlayAttachedForInit;
        // 挂载后无条件重取视觉:之前缓存的可能是挂载前创建、已被替换的实例
        _visual = ElementComposition.GetElementVisual(_overlay);
        if (_visual is not null && !_vm.ShowNowPlaying)
            PinClosed();
    }

    private bool TryInitVisual()
    {
        if (_visual is not null) return true;
        _visual = ElementComposition.GetElementVisual(_overlay);
        return _visual is not null;
    }

    /// <summary>布局就绪/尺寸变化后调用:关闭态把屏幕外位置从兜底值更新为真实高度(仍无动画,不可见)。</summary>
    public void UpdateClosedPosition()
    {
        if (_vm.ShowNowPlaying) return;
        if (!TryInitVisual()) return; // 未挂载:AttachedToVisualTree 回调会补
        PinClosed();
    }

    public void Dispose()
    {
        _vm.PropertyChanged -= OnViewModelPropertyChanged;
        _overlay.AttachedToVisualTree -= OnOverlayAttachedForInit;
        _visual?.StopAnimation("Translation");
    }

    private double ClosedY
    {
        get
        {
            var h = _overlay.Bounds.Height;
            return (h > 0 ? h : s_offScreenGuard) + s_slideMargin;
        }
    }

    /// <summary>无动画摆到关闭位(停止在途动画,防止动画期间属性写入被覆盖)。</summary>
    private void PinClosed()
    {
        if (_visual is null) return;
        _visual.StopAnimation("Translation");
        _current = new Vector3(0, (float)ClosedY, 0);
        _visual.Translation = _current;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.ShowNowPlaying)) return;
        if (!TryInitVisual()) return; // 未挂载:无法动画;AttachedToVisualTree 回调会按当前状态补位

        var to = _vm.ShowNowPlaying ? Vector3.Zero : new Vector3(0, (float)ClosedY, 0);
        // ⚠ 起点用自己维护的 _current,不能读 _visual.Translation 属性:
        //   合成动画结束后视觉停在末帧,但属性 getter 返回的仍是动画前的基值 ——
        //   直接读它会得到"关闭位",关闭动画变成"从关闭位到关闭位"= 零位移瞬跳(实测)。
        var from = _current;
        if (_vm.ShowNowPlaying)
        {
            // 兜底偏移(挂载后布局未跑就开详情页)远大于真实高度:起点改用真实闭位,避免从十万像素外起飞
            var closedY = (float)ClosedY;
            if (from.Y > closedY)
                from = new Vector3(0, closedY, 0);
        }

        // 基值先设为目标位(同 FlyoutOpenAnimation 的"结束帧=基值"约定):
        // 动画播完回落基值 = 目标位,无缝交接不回跳;动画从旧位起播,同批次应用无闪帧。
        _current = to;
        _visual.Translation = to;

        var slide = _visual.Compositor.CreateVector3KeyFrameAnimation();
        slide.Duration = s_slideDuration;
        slide.InsertKeyFrame(0f, from);
        slide.InsertKeyFrame(1f, to, s_slideEasing);
        _visual.StartAnimation("Translation", slide);
    }
}
