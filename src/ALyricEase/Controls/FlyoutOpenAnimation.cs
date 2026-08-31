using System;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Logging;
using Avalonia.LogicalTree;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ALyricEase.Controls;

/// <summary>
/// 仿原版 WinUI3 的菜单/Flyout 打开动画(合成动画,走合成器线程):
/// 83ms 线性淡入 + 250ms cubic-bezier(0.1,0.9,0.2,1) 减速滑入,
/// 对应原版 FlyoutBase 的 PopupThemeTransition(g_entranceThemeOffset=50)。
///
/// 只有纵向滑入(原版菜单过渡 MenuPopupThemeTransition 仅 Top/Bottom 两个方向,无左右)。
/// 方向按弹窗最终位置相对锚点(PlacementTarget)判断:菜单在锚点下方→向下滑,在上方→向上滑;
/// 右键菜单是指针放置(锚点是目标内一点),用弹窗上/下端点是否落在目标竖向范围内判断上/下;
/// 子菜单顶部与父项对齐→向下滑,被屏幕底部顶到上方→向上滑。翻转(下方空间不足时往上开)因此自动正确。
///
/// 原版的"从锚边揭示"由独立弹窗窗口的裁剪天然实现:初始偏移让表面超出锚边一侧,
/// 越界部分被窗口裁掉,随滑动逐渐露出。启动时机在 PopupRoot.PositionChanged(Win32 对隐藏窗口
/// SetWindowPos 也会触发,此时窗口尚未显示)→ 方向已定且首帧未渲染,无闪现。
///
/// 用法:样式对弹层表面(FlyoutPresenter / MenuFlyoutPresenter / 子菜单 Popup#PART_Popup Border)设 IsEnabled=True;
/// 弹窗关闭即销毁宿主,样式随 attach 重新应用,故每次打开都会重放。
/// </summary>
public class FlyoutOpenAnimation
{
    private FlyoutOpenAnimation()
    {
    }

    private const double Offset = 50;
    private static readonly TimeSpan SlideDuration = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(83);

    // SplineEasing 的四参构造函数有 bug(y2 被误赋给 y1,Y2 永不生效),必须用属性初始化
    private static readonly SplineEasing SlideEasing = new() { X1 = 0.1, Y1 = 0.9, X2 = 0.2, Y2 = 1.0 };

    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<FlyoutOpenAnimation, Visual, bool>("IsEnabled");

    /// <summary>每次打开只启动一次的去重标记(attach 时清空)。</summary>
    private static readonly ConditionalWeakTable<Visual, StrongBox<bool>> StartFlags = new();

    /// <summary>弹窗窗口最终位置(PositionChanged 事件携带,WindowBase 无公开 Position 属性)。</summary>
    private static readonly ConditionalWeakTable<Visual, StrongBox<PixelPoint>> PopupPositions = new();

    /// <summary>探针诊断:每次动画实际启动时回报表面与起始偏移。</summary>
    internal static event Action<Visual, double, double>? Started;

    static FlyoutOpenAnimation()
    {
        IsEnabledProperty.Changed.AddClassHandler<Visual>((surface, e) =>
        {
            if (e.NewValue is not true)
            {
                return;
            }

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
            StartFlags.Remove(surface);
        }
    }

    private static void Hook(Visual surface)
    {
        switch (TopLevel.GetTopLevel(surface))
        {
            case PopupRoot popupRoot:
            {
                // 定位完成即启动(窗口仍隐藏);窗口从 (0,0) 起步,定位必然变更,事件必然触发
                EventHandler<PixelPointEventArgs> onPositioned = null!;
                onPositioned = (_, e) =>
                {
                    popupRoot.PositionChanged -= onPositioned;
                    PopupPositions.GetOrCreateValue(surface).Value = e.Point;
                    TryStart(surface);
                };
                popupRoot.PositionChanged += onPositioned;

                // 兜底:个别平台可能不触发 PositionChanged → Opened 时补启动(去重表防重启)
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
        return ComputeStartOffset(popupRect.Value, targetRect);
    }

    /// <summary>探针诊断:最近一次方向计算的矩形/目标解析结果。</summary>
    internal static (bool HasPopupRect, bool HasTarget) LastResolved { get; private set; }

    /// <summary>
    /// 方向规则(屏幕物理像素,仅纵向——原版菜单过渡只有 Top/Bottom 两个方向):
    /// 整体在锚点下方→向下滑,整体在上方→向上滑;重叠时(指针放置/子菜单)用弹窗上/下端点
    /// 是否落在目标竖向范围内判断;子菜单顶部与父项对齐(Y 相等)走兜底→向下滑。
    /// </summary>
    internal static (double dx, double dy) ComputeStartOffset(PixelRect popup, PixelRect target)
    {
        const double eps = 2;
        if (popup.Y >= target.Bottom - eps)
        {
            return (0, -Offset);
        }

        if (popup.Bottom <= target.Y + eps)
        {
            return (0, Offset);
        }

        // 重叠:锚点是目标内一点(指针放置的右键菜单/顶对齐的子菜单被顶起时),
        // 弹窗上端或下端落在目标竖向范围内分别对应下开/上开
        if (popup.Y > target.Y && popup.Y < target.Bottom)
        {
            return (0, -Offset);
        }

        if (popup.Bottom > target.Y && popup.Bottom < target.Bottom)
        {
            return (0, Offset);
        }

        return (0, -Offset);
    }

    private static void StartComposition(Visual surface, double dx, double dy)
    {
        var visual = ElementComposition.GetElementVisual(surface);
        if (visual is null)
        {
            Logger.TryGet(LogEventLevel.Warning, LogArea.Control)?.Log(surface,
                "FlyoutOpenAnimation: 合成视觉不可用,跳过打开动画");
            return;
        }

        var compositor = visual.Compositor;
        var baseOffset = visual.Offset;

        // 结束帧 = 基值:动画播完自动移出时钟并回落基值,无缝交接,无需手动 Stop
        var slide = compositor.CreateVector3KeyFrameAnimation();
        slide.Duration = SlideDuration;
        slide.InsertKeyFrame(0f, new Vector3((float)(baseOffset.X + dx), (float)(baseOffset.Y + dy), 0f));
        slide.InsertKeyFrame(1f, new Vector3((float)baseOffset.X, (float)baseOffset.Y, 0f), SlideEasing);
        visual.StartAnimation("Offset", slide);

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.Duration = FadeDuration;
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, 1f);
        visual.StartAnimation("Opacity", fade);
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
