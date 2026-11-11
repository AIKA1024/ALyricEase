using System.Diagnostics;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ALyricEase.Views;

/// <summary>
/// 应用网络代理设置对话框。
/// <para>
/// 开合由宿主 <c>ModalVisibilityTransition</c> 统一协调;这里只管「切到手动配置时卡片高度变化」
/// 的过渡 —— 布局一次跳到最终尺寸,背景层用合成缩放从旧尺寸反向还原,内容层前半段隐藏、
/// 后半段淡入。与 <see cref="DownloadSongDialogView"/> 同一套 morph:不逐帧改 Height,
/// 免得整棵子树每帧重排;缩放只作用在背景层,文字保持正常比例。
/// </para>
/// </summary>
public partial class ProxySettingsDialogView : UserControl
{
    private static readonly TimeSpan MorphDuration = TimeSpan.FromMilliseconds(300);
    private static readonly SplineEasing MorphEasing = new(0.215, 0.61, 0.355, 1);

    private readonly Stopwatch _morphClock = new();
    private Size? _pendingFrom;
    private Size _lastFrom;
    private Size _lastTo;
    private bool _isMorphing;
    private int _morphGeneration;
    private IDisposable? _primedOpacity;

    /// <summary>尺寸过渡进行中(供无头回归断言)。</summary>
    internal bool IsMorphing => _pendingFrom is not null || _isMorphing;

    /// <summary>过渡状态明细,断言失败时用来定位是哪一半没清掉。</summary>
    internal string MorphDiagnostics =>
        $"pending={_pendingFrom is not null}, running={_isMorphing}, " +
        $"clock={_morphClock.ElapsedMilliseconds}ms";

    public ProxySettingsDialogView()
    {
        InitializeComponent();

        ManualSection.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty) BeginMorph();
        };
        DialogCard.LayoutUpdated += (_, _) => StartMorphAfterLayout();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && !IsVisible) ResetMorph();
        };
    }

    /// <summary>
    /// 手动配置区显隐的那一刻:布局还没跑,<see cref="Visual.Bounds"/> 仍是旧尺寸,
    /// 趁现在记下来。内容同步压透明 —— 等下一次布局就晚了,会先闪一帧旧布局。
    /// </summary>
    private void BeginMorph()
    {
        if (!IsEffectivelyVisible || !IsEffectivelyEnabled || !this.IsAttachedToVisualTree()) return;
        if (DialogCard.Bounds.Height <= 0) return;

        var from = CurrentVisualSize();
        ResetMorph();
        _pendingFrom = from;
        _primedOpacity = DialogContent.SetValue(OpacityProperty, 0d, BindingPriority.Animation);
    }

    private void StartMorphAfterLayout()
    {
        if (_pendingFrom is not { } from) return;

        var to = DialogCard.Bounds.Size;
        if (to.Width <= 0 || to.Height <= 0) return;

        var surface = ElementComposition.GetElementVisual(DialogSurface);
        var content = ElementComposition.GetElementVisual(DialogContent);
        if (surface is null || content is null || !IsEffectivelyVisible || !IsEffectivelyEnabled)
        {
            ResetMorph();
            return;
        }

        _pendingFrom = null;
        _primedOpacity?.Dispose();
        _primedOpacity = null;

        // 卡片是居中的,所以从中心对称伸展:高度变多少,上下各伸一半。
        // 宽高比各自独立,文字不参与缩放,不会被压扁。
        surface.CenterPoint = new Vector3D(to.Width / 2, to.Height / 2, 0);
        surface.Scale = new Vector3D(1, 1, 1);
        var stretch = surface.Compositor.CreateVector3DKeyFrameAnimation();
        stretch.Duration = MorphDuration;
        stretch.InsertKeyFrame(0f, new Vector3D(from.Width / to.Width, from.Height / to.Height, 1));
        stretch.InsertKeyFrame(1f, new Vector3D(1, 1, 1), MorphEasing);
        surface.StartAnimation("Scale", stretch);

        // 前半段完全隐藏:此时背景还没长到位,内容(尤其是刚展开的手动配置区)会悬在卡片外。
        // 0.5 之后淡入,与下载弹窗同一节奏。
        content.Opacity = 1;
        var fade = content.Compositor.CreateScalarKeyFrameAnimation();
        fade.Duration = MorphDuration;
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(0.5f, 0f);
        fade.InsertKeyFrame(1f, 1f, MorphEasing);
        content.StartAnimation("Opacity", fade);

        _lastFrom = from;
        _lastTo = to;
        _morphClock.Restart();
        _isMorphing = true;

        // 收尾用线程池计时 + 显式 Post 回 UI:DispatcherTimer 依赖 dispatcher 主循环,
        // 无头回归里 RunJobs 不推进它,会永远停在"动画中"(内容一直透明)。
        _ = CompleteMorphAsync(++_morphGeneration);
    }

    private async Task CompleteMorphAsync(int generation)
    {
        await Task.Delay(MorphDuration).ConfigureAwait(false);
        Dispatcher.UIThread.Post(() =>
        {
            if (generation == _morphGeneration) ResetMorph();
        });
    }

    /// <summary>
    /// 动画进行中被再次切换时,用曲线反推当前"看得见"的尺寸当作新起点,
    /// 否则会从上一次的目标尺寸硬跳。合成的 Scale 读的是基值(终值),量不到实时值,
    /// 只能自己按同一条曲线算。
    /// </summary>
    private Size CurrentVisualSize()
    {
        var layout = DialogCard.Bounds.Size;
        if (!_isMorphing) return layout;

        var progress = Math.Clamp(
            _morphClock.Elapsed.TotalMilliseconds / MorphDuration.TotalMilliseconds, 0d, 1d);
        var eased = MorphEasing.Ease(progress);
        return new Size(
            _lastFrom.Width + ((_lastTo.Width - _lastFrom.Width) * eased),
            _lastFrom.Height + ((_lastTo.Height - _lastFrom.Height) * eased));
    }

    private void ResetMorph()
    {
        _pendingFrom = null;
        _isMorphing = false;
        _morphClock.Reset();

        // 先放开 UI 层的临时透明度,再停合成动画:反过来会让内容在"UI 层仍是 0"的
        // 状态下多停一帧。基值也要先写回 1,StopAnimation 才能落到位。
        _primedOpacity?.Dispose();
        _primedOpacity = null;

        if (ElementComposition.GetElementVisual(DialogSurface) is { } surface)
        {
            surface.Scale = new Vector3D(1, 1, 1);
            surface.StopAnimation("Scale");
        }
        if (ElementComposition.GetElementVisual(DialogContent) is { } content)
        {
            content.Opacity = 1;
            content.StopAnimation("Opacity");
        }
    }
}
