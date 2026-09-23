using System;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Logging;
using Avalonia.LogicalTree;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ALyricEase.Controls;

/// <summary>
/// 仿原版 WinUI3 的菜单/Flyout 打开动画(合成动画,走合成器线程)。
/// 参数与 WinUI3 框架源码 MenuPopupThemeTransition 对齐(2026-09-23 从
/// microsoft-ui-xaml 仓库 LayoutTransition_partial.cpp / MenuPopupThemeTransition_Partial.h / MenuFlyout_Partial.cpp 考据):
/// · 滑入 250ms(s_OpenDuration),缓动 cubic-bezier(0,0,0,1)(强 ease-out:起步速度无穷大,长减速尾);
/// · 位移 = 弹层高度 × ClosedRatio(0.5)(原版 initialTranslateY = OpenedLength × ClosedRatio);
/// · 菜单表面全程不透明 —— 原版只对遮罩层(overlay)做 83ms 淡入,表面靠裁剪揭示;
/// · 揭示 = 表面平移 + ClipTranslate 反向 + Border ScaleY 0.5→1,我们用弹窗窗口裁剪近似前两者。
/// </summary>
///
/// 起始偏移取弹层的完整高度,使首帧整个表面都在弹窗窗口的裁剪区外。
/// 揭示效果靠"弹窗窗口裁掉越界部分"实现,所以开局露出多少取决于【偏移 / 弹层高度】:
/// 固定 50px 时,2 项的短菜单遮掉 56%(像从 0 滑出),而播放条 10 项的长菜单只遮掉约 17%
/// (50% 高度仍会让半个菜单在首帧出现)。完整高度偏移才能保证从裁剪区外开始。
///
/// 只有纵向滑入(原版菜单过渡 MenuPopupThemeTransition 仅 Top/Bottom 两个方向,无左右)。
/// 方向按弹窗最终位置相对锚点(PlacementTarget)判断:菜单在锚点下方→向下滑,在上方→向上滑;
/// 右键菜单是指针放置(锚点是目标内一点),用弹窗上/下端点是否落在目标竖向范围内判断上/下;
/// 子菜单顶部与父项对齐→向下滑,被屏幕底部顶到上方→向上滑。翻转(下方空间不足时往上开)因此自动正确。
///
/// 原版的"从锚边揭示"由独立弹窗窗口的裁剪天然实现:初始偏移让表面超出锚边一侧,
/// 越界部分被窗口裁掉,随滑动逐渐露出。PopupRoot.PositionChanged 只记录最终位置;
/// 动画等 Popup.Opened 后再启动,避免在未显示的合成树上提前播放。Android 使用内联 overlay,
/// 布局前先同步把合成层预置为透明,避免最终态在动画启动前闪现一帧。
///
/// 用法:样式对弹层表面(FlyoutPresenter / MenuFlyoutPresenter / 子菜单 Popup#PART_Popup Border)设 IsEnabled=True;
/// 弹窗关闭即销毁宿主,样式随 attach 重新应用,故每次打开都会重放。
/// </summary>
public class FlyoutOpenAnimation
{
    private FlyoutOpenAnimation()
    {
    }

    /// <summary>无法取到弹层高度时的兜底偏移(= 50 × ClosedRatio)。</summary>
    private const double Offset = 25;

    // WinUI3 原版参数(2026-09-23 考据自 microsoft-ui-xaml 仓库,并经用户对原版实测确认):
    // · 时长 s_OpenDuration = 250ms(MenuPopupThemeTransition_Partial.h);
    // · 缓动 cubic-bezier(0, 0, 0, 1)(LayoutTransition_partial.cpp:easing.cp3.X = 0.0f,注释原文同款;
    //   强 ease-out —— 起步速度无穷大、长减速尾:前 20% 时间走完 ~63%,前 32% 时间 ~80%);
    // · 位移 = 弹层高度 × ClosedRatio = 0.5(MenuFlyout 传入 closedRatioConstant=0.5):
    //   原版的 ClipTranslateY(+0.5H→0)与表面 TranslateY(-0.5H→0)互相抵消,净可见效果就是
    //   "首帧露出半张菜单 → 表面滑 0.5H 到位"(用户实测原版确认),窗口裁剪下单通道 0.5H 位移即等效。
    // ⚠ 位移别按"双向对开"改成全高:全高首帧全空,和原版"露半张"观感不符(2026-09-23 实测过一轮)。
    private static readonly TimeSpan SlideDuration = TimeSpan.FromMilliseconds(250);
    private static readonly SplineEasing SlideEasing = new() { X1 = 0, Y1 = 0, X2 = 0, Y2 = 1 };
    private const double ClosedRatio = 0.5;

    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<FlyoutOpenAnimation, Visual, bool>("IsEnabled");

    /// <summary>每次打开只启动一次的去重标记(attach 时清空)。</summary>
    private static readonly ConditionalWeakTable<Visual, StrongBox<bool>> StartFlags = new();

    /// <summary>布局完成前施加在 Opacity 上的临时动画优先级值；Dispose 后恢复原值源。</summary>
    private static readonly ConditionalWeakTable<Visual, IDisposable> PrimedOpacities = new();

    /// <summary>弹窗窗口最终位置(PositionChanged 事件携带,WindowBase 无公开 Position 属性)。</summary>
    private static readonly ConditionalWeakTable<Visual, StrongBox<PixelPoint>> PopupPositions = new();

    /// <summary>探针诊断:每次动画实际启动时回报表面与起始偏移。</summary>
    internal static event Action<Visual, double, double>? Started;

    /// <summary>探针诊断:表面在首次合成前已进入透明预备态。</summary>
    internal static event Action<Visual>? Primed;

    static FlyoutOpenAnimation()
    {
        IsEnabledProperty.Changed.AddClassHandler<Visual>((surface, e) =>
        {
            if (e.NewValue is not true)
            {
                RestorePrimedOpacity(surface);
                surface.AttachedToVisualTree -= OnSurfaceAttached;
                surface.DetachedFromVisualTree -= OnSurfaceDetached;
                return;
            }

            // 必须早于 attach：Android 会在 AttachedToVisualTree 后建立并同步合成视觉，
            // 此时才直接写 CompositionVisual 会被首次同步的 Opacity=1 覆盖。
            PrimeSurface(surface);

            // 样式应用可能早于或晚于 attach,两种顺序都要覆盖;-=+= 保证订阅幂等
            surface.AttachedToVisualTree -= OnSurfaceAttached;
            surface.AttachedToVisualTree += OnSurfaceAttached;
            surface.DetachedFromVisualTree -= OnSurfaceDetached;
            surface.DetachedFromVisualTree += OnSurfaceDetached;

            if (TopLevel.GetTopLevel(surface) is not null)
            {
                Hook(surface);
            }
        });
    }

    public static void SetIsEnabled(Visual visual, bool value) => visual.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(Visual visual) => visual.GetValue(IsEnabledProperty);

    private static void OnSurfaceAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Visual surface)
        {
            Hook(surface);
        }
    }

    private static void OnSurfaceDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Visual surface)
        {
            RestorePrimedOpacity(surface);
            StartFlags.Remove(surface);
        }
    }

    private static void Hook(Visual surface)
    {
        // Android 的 overlay 弹窗要等布局后才有高度和方向，但不能等到那时才设置起始视觉：
        // 否则 Popup 会先以最终态完成一次合成，再从 0 播放动画，形成明显闪现。
        PrimeSurface(surface);

        switch (TopLevel.GetTopLevel(surface))
        {
            case PopupRoot popupRoot:
            {
                // 弹窗从 (0,0) 起步,定位变更时先记住最终位置。
                EventHandler<PixelPointEventArgs> onPositioned = null!;
                onPositioned = (_, e) =>
                {
                    popupRoot.PositionChanged -= onPositioned;
                    PopupPositions.GetOrCreateValue(surface).Value = e.Point;
                };
                popupRoot.PositionChanged += onPositioned;

                // 定位时只记录方向;Show 完成后再启动。过早在未显示的
                // 合成树上起播,会让首次可见提交直接落在动画中后段。
                if (FindPopup(surface) is { } popup)
                {
                    EventHandler onOpened = null!;
                    onOpened = (_, _) =>
                    {
                        popup.Opened -= onOpened;
                        TryStart(surface);
                    };
                    popup.Opened += onOpened;
                }

                break;
            }

            case TopLevel:
                // overlay 内联弹窗(Android 等):无独立窗口与定位事件,布局完成后按当前几何启动
                Dispatcher.UIThread.Post(() => TryStart(surface), DispatcherPriority.Loaded);
                break;

            default:
                TryStart(surface);
                break;
        }
    }

    private static void TryStart(Visual surface)
    {
        // Android overlay 使用 Loaded 队列；若弹层在回调前已关闭，不得给已脱树表面留下 started 状态。
        if (TopLevel.GetTopLevel(surface) is null)
        {
            return;
        }

        var box = StartFlags.GetOrCreateValue(surface);
        if (box.Value)
        {
            return;
        }

        box.Value = true;

        var (dx, dy) = ComputeStart(surface);
        StartComposition(surface, dx, dy);
        Started?.Invoke(surface, dx, dy);
    }

    private static (double dx, double dy) ComputeStart(Visual surface)
    {
        var popupRect = GetPopupPixelRect(surface);
        var target = ResolveTarget(surface);
        if (popupRect is null || target is null)
        {
            LastResolved = (false, false);
            return (0, -Offset);
        }

        var targetRect = GetPixelRect(target);
        LastResolved = (true, true);
        return ComputeStartOffset(popupRect.Value, targetRect, GetSurfaceHeight(surface));
    }

    /// <summary>探针诊断:最近一次方向计算的矩形/目标解析结果。</summary>
    internal static (bool HasPopupRect, bool HasTarget) LastResolved { get; private set; }

    /// <summary>
    /// 入场偏移:取弹层的完整高度,使表面首帧位于弹窗裁剪区外。
    /// 50% 偏移仍会让长菜单的一半在首帧出现,无法解决播放条菜单的突现观感。
    /// </summary>
    internal static double ComputeEntranceOffset(double surfaceHeight)
    {
        if (surfaceHeight <= 0 || !double.IsFinite(surfaceHeight))
        {
            return Offset;
        }

        // 半高(ClosedRatio=0.5):首帧露出半张菜单,表面滑 0.5H 到位 —— 原版净可见运动学(见类注释)。
        return surfaceHeight * ClosedRatio;
    }

    /// <summary>
    /// 方向规则(屏幕物理像素,仅纵向——原版菜单过渡只有 Top/Bottom 两个方向):
    /// 整体在锚点下方→向下滑,整体在上方→向上滑;重叠时(指针放置/子菜单)用弹窗上/下端点
    /// 是否落在目标竖向范围内判断;子菜单顶部与父项对齐(Y 相等)走兜底→向下滑。
    /// </summary>
    internal static (double dx, double dy) ComputeStartOffset(PixelRect popup, PixelRect target, double surfaceHeight)
    {
        var distance = ComputeEntranceOffset(surfaceHeight);
        const double eps = 2;
        if (popup.Y >= target.Bottom - eps)
        {
            return (0, -distance);
        }

        if (popup.Bottom <= target.Y + eps)
        {
            return (0, distance);
        }

        // 重叠:锚点是目标内一点(指针放置的右键菜单/顶对齐的子菜单被顶起时),
        // 弹窗上端或下端落在目标竖向范围内分别对应下开/上开
        if (popup.Y > target.Y && popup.Y < target.Bottom)
        {
            return (0, -distance);
        }

        if (popup.Bottom > target.Y && popup.Bottom < target.Bottom)
        {
            return (0, distance);
        }

        return (0, -distance);
    }

    /// <summary>弹层表面的高度(DIP)。揭示靠弹窗窗口裁剪,裁剪边界就是窗口,
    /// 所以优先取弹窗宿主尺寸(此时表面可能尚未 Arrange,Bounds 还是 0)。</summary>
    private static double GetSurfaceHeight(Visual surface)
    {
        switch (TopLevel.GetTopLevel(surface))
        {
            case PopupRoot popupRoot:
                // ClientSize 是 DIP,弹窗窗口按内容尺寸创建,即表面高度
                return popupRoot.ClientSize.Height;

            case TopLevel when surface.GetVisualAncestors()
                .OfType<OverlayPopupHost>().LastOrDefault() is { } host:
                return host.Bounds.Height;

            default:
                return surface.Bounds.Height;
        }
    }

    private static void StartComposition(Visual surface, double dx, double dy)
    {
        var visual = ElementComposition.GetElementVisual(surface);
        if (visual is null)
        {
            RestorePrimedOpacity(surface);
            Logger.TryGet(LogEventLevel.Warning, LogArea.Control)?.Log(surface,
                "FlyoutOpenAnimation: 合成视觉不可用,跳过打开动画");
            return;
        }

        var compositor = visual.Compositor;
        var baseOffset = visual.Offset;
        var baseOpacity = RestorePrimedOpacity(surface, visual);

        // 结束帧 = 基值:动画播完自动移出时钟并回落基值,无缝交接,无需手动 Stop。
        // 原版菜单表面全程不透明(只有遮罩层 83ms 淡入,我们没有遮罩层),靠裁剪揭示即可:
        // 恢复基透明度后只播位移动画,首帧即全不透明的半张菜单从锚边滑出。
        var slide = compositor.CreateVector3KeyFrameAnimation();
        slide.Duration = SlideDuration;
        slide.InsertKeyFrame(0f, new Vector3((float)(baseOffset.X + dx), (float)(baseOffset.Y + dy), 0f));
        slide.InsertKeyFrame(1f, new Vector3((float)baseOffset.X, (float)baseOffset.Y, 0f), SlideEasing);
        visual.StartAnimation("Offset", slide);
    }

    /// <summary>在表面 attach 前用可撤销的动画优先级值预隐藏；不会替换原有样式或绑定。</summary>
    private static void PrimeSurface(Visual surface)
    {
        if (PrimedOpacities.TryGetValue(surface, out _) ||
            StartFlags.TryGetValue(surface, out var start) && start.Value)
        {
            return;
        }

        var token = surface.SetValue(Visual.OpacityProperty, 0d, BindingPriority.Animation);
        if (token is null)
        {
            return;
        }

        PrimedOpacities.Add(surface, token);

        // 已 attach 时同时更新现有合成视觉；未 attach 时属性系统会在创建视觉时带入 0。
        if (ElementComposition.GetElementVisual(surface) is { } visual)
        {
            visual.Opacity = 0f;
        }

        Primed?.Invoke(surface);
    }

    /// <summary>动画启动或提前关闭时恢复原始合成透明度。</summary>
    private static float RestorePrimedOpacity(Visual surface, CompositionVisual? visual = null)
    {
        if (!PrimedOpacities.TryGetValue(surface, out var token))
        {
            return visual?.Opacity ?? (float)surface.Opacity;
        }

        PrimedOpacities.Remove(surface);
        token.Dispose();
        var baseOpacity = (float)surface.Opacity;
        visual ??= ElementComposition.GetElementVisual(surface);
        if (visual is not null)
        {
            visual.Opacity = baseOpacity;
        }

        return baseOpacity;
    }

    private static Popup? FindPopup(Visual surface) =>
        surface.GetLogicalAncestors().OfType<Popup>().FirstOrDefault();

    private static Control? ResolveTarget(Visual surface) =>
        FindPopup(surface) is { } popup ? popup.PlacementTarget ?? popup.Parent as Control : null;

    private static PixelRect? GetPopupPixelRect(Visual surface)
    {
        switch (TopLevel.GetTopLevel(surface))
        {
            case PopupRoot popupRoot:
            {
                // 弹窗矩形 = PositionChanged 记录的窗口位置 + 客户端尺寸 × 缩放
                var pos = PopupPositions.GetOrCreateValue(surface).Value;
                var size = popupRoot.ClientSize;
                var scale = popupRoot.RenderScaling;
                return new PixelRect(pos, new PixelSize(
                    (int)Math.Round(size.Width * scale),
                    (int)Math.Round(size.Height * scale)));
            }

            case TopLevel topLevel when surface.GetVisualAncestors()
                .OfType<OverlayPopupHost>().LastOrDefault() is { } host:
            {
                // overlay 弹窗:无独立窗口,直接取宿主自身的屏幕矩形
                return GetPixelRect(host);
            }

            default:
                return null;
        }
    }

    private static PixelRect GetPixelRect(Visual visual)
    {
        // 不用 PointToScreen:headless 的实现不含窗口 Position,桌面/headless 统一用
        // "窗口 Position + 客户端坐标 × 缩放"换算到屏幕物理坐标(两者数学上等价)
        var topLevel = TopLevel.GetTopLevel(visual);
        if (topLevel is null)
        {
            return default;
        }

        var topLeft = visual.TranslatePoint(new Point(0, 0), topLevel);
        var bottomRight = visual.TranslatePoint(new Point(visual.Bounds.Width, visual.Bounds.Height), topLevel);
        if (topLeft is not { } a || bottomRight is not { } b)
        {
            return default;
        }

        var scale = topLevel.RenderScaling;
        var pos = topLevel is Window window ? window.Position : default;
        return new PixelRect(
            new PixelPoint(pos.X + (int)Math.Round(a.X * scale), pos.Y + (int)Math.Round(a.Y * scale)),
            new PixelPoint(pos.X + (int)Math.Round(b.X * scale), pos.Y + (int)Math.Round(b.Y * scale)));
    }
}
