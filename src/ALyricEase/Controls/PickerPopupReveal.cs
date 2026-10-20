using System;
using System.Diagnostics;
using System.Linq;
using ALyricEase.Infrastructure;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ALyricEase.Controls;

/// <summary>
/// WinUI 风格 ComboBox 下拉层行为，挂在模板里的 <c>PART_Popup</c> 上
/// （<c>PickerPopupReveal.Enabled="True"</c>），因此所有使用 FluentPickerTheme 的
/// ComboBox 自动生效，不需要为每个用法换控件类型。
///
/// 两个行为，和 WinUI 3 Gallery 对齐：
/// 1. **位置**：弹出层不再固定贴在控件下沿，而是整体上下平移，让**选中项**盖在控件位置
///    （WinUI 的 ComboBox 下拉就是把当前项对到控件上）。原生弹窗用屏幕坐标实测；
///    Overlay 用相对布局坐标，避免在宿主尚未完成移动时读取旧的屏幕位置。
/// 2. **动画**：内容以选中项中心为原点向两侧"揭开"（见 <see cref="RevealBorder"/>），
///    揭示结束之后阴影才淡入。弹出层在定位完成前是完全裁剪掉的，不会闪一下错误位置。
///
/// 时序比 WinUI 稍快一档（用户要求"快一点点"）：揭示 200ms（EaseOutCubic），
/// 阴影延迟到揭示结束再 220ms 淡入。
/// </summary>
public static class PickerPopupReveal
{
    private const double RevealMs = 200;
    private const double ShadowDelayMs = 200;
    private const double ShadowMs = 220;

    public static readonly AttachedProperty<bool> EnabledProperty =
        AvaloniaProperty.RegisterAttached<Popup, bool>("Enabled", typeof(PickerPopupReveal));

    static PickerPopupReveal()
    {
        EnabledProperty.Changed.AddClassHandler<Popup>((popup, e) =>
        {
            if (e.GetNewValue<bool>())
            {
                _ = new Controller(popup);
            }
        });
    }

    public static void SetEnabled(Popup popup, bool value) => popup.SetValue(EnabledProperty, value);

    public static bool GetEnabled(Popup popup) => popup.GetValue(EnabledProperty);

    private sealed class Controller
    {
        private readonly Popup _popup;
        private RevealBorder? _surface;
        private Border? _shadow;

        private readonly Stopwatch _clock = new();
        private bool _framePending;
        private bool _pacerHeld;
        private ScrollViewer? _scroll;
        private int _retries;
        private bool _clockStarted;
        private bool _prepared;

        public Controller(Popup popup)
        {
            _popup = popup;

            popup.PropertyChanged += OnPopupPropertyChanged;
            popup.Opened += OnOpened;
            popup.Closed += OnClosed;

            // 模板刚建时 Child 还没挂上；挂上那一刻就把内容收起来，避免首帧闪在错误位置。
            ResolveParts();
            ResetVisuals();
        }

        private ComboBox? Combo => _popup.TemplatedParent as ComboBox;

        /// <summary>
        /// 弹层内容在模板里是本 Popup 的子级，属性赋值早于内容挂上，
        /// 所以只在 Child 变化/Opened 时解析。模板重套时也自动拿到新的一批。
        /// </summary>
        private void ResolveParts()
        {
            _surface = null;
            _shadow = null;

            if (_popup.Child is not Panel root)
            {
                return;
            }

            foreach (var child in root.Children)
            {
                if (child is RevealBorder surface) _surface = surface;
                else if (child is Border border && border.Name == "PART_DropDownShadow") _shadow = border;
            }
        }

        private void OnPopupPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property != Popup.ChildProperty)
            {
                return;
            }

            ResolveParts();
            if (!_popup.IsOpen)
            {
                ResetVisuals();
            }
        }

        private void OnOpened(object? sender, EventArgs e)
        {
            ResolveParts();
            _prepared = false;
            _retries = 0;
            _scroll = null;
            ResetVisuals();

            if (_surface is null)
            {
                return;
            }

            var combo = Combo;

            // 弹出层至少和控件一样宽（WinUI 的 DropDownContentMinWidth 语义）。
            var width = combo?.Bounds.Width ?? 0;
            if (width > 0)
            {
                _surface.MinWidth = width;
            }

            // MaxDropDownHeight 限制的是内容高度，不含阴影预留边距。
            if (combo is not null)
            {
                _surface.MaxHeight = combo.MaxDropDownHeight;
            }

            // 先同步跑一遍布局再量：弹层窗口刚建好时内容还没排过版，
            // 等 Dispatcher 的 Render 队列在"整层被剪成不可见"时会拖到下一帧才有响应。
            TryPrepare();
            if (!_prepared)
            {
                Dispatcher.UIThread.Post(TryPrepare, DispatcherPriority.Render);
            }
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            ReleasePacer();
            _prepared = false;
            ResetVisuals();

            // 下一次打开重新从"贴住控件下沿"开始量，避免偏移逐次累积。
            _popup.VerticalOffset = 0;
            _popup.HorizontalOffset = 0;
        }

        private void TryPrepare()
        {
            if (_prepared || _surface is null || !_popup.IsOpen)
            {
                return;
            }

            _surface.UpdateLayout();

            var combo = Combo;
            if (combo is not null && _surface.Bounds.Height > 0)
            {
                var index = combo.SelectedIndex >= 0 ? combo.SelectedIndex : 0;
                if (combo.ContainerFromIndex(index) is Control container &&
                    container.Bounds.Height > 0)
                {
                    // 列表比一屏高时，选中项要落在"弹出层整体能留在屏幕内"的位置，
                    // 否则弹层会被顶到屏幕外，选中项反而看不见。滚动可能因为 Extent
                    // 还没量全而一次滚不到位，所以允许几轮，无进展就停。
                    CapHeightToScreen(combo, container);

                    for (var attempt = 0; attempt < 4; attempt++)
                    {
                        var top = container.TranslatePoint(default, _surface)?.Y ?? 0;
                        if (!TryBringSelectedItemIntoRange(combo, top, container.Bounds.Height, _surface.Bounds.Height))
                        {
                            break;
                        }

                        _surface.UpdateLayout();
                    }

                    var itemTop = container.TranslatePoint(default, _surface)?.Y ?? 0;
                    Prepare(AlignToSelectedItem(combo, container, itemTop));
                    return;
                }
            }

            if (++_retries < 20)
            {
                // 容器还没实化时也要保证 UI 线程按时醒来，否则重试会被 79ms 的长睡拖住。
                AcquirePacer();
                DispatcherTimer.RunOnce(TryPrepare, TimeSpan.FromMilliseconds(16), DispatcherPriority.Render);
            }
            else
            {
                // 兜底：容器迟迟没实现（虚拟化/无选区）也要把内容显示出来。
                Prepare(double.NaN);
            }
        }

        /// <summary>
        /// 控件贴近屏幕下沿、而选中项又靠列表顶部时，"让选中项盖住控件"要求的弹层高度
        /// 会超出屏幕。此时缩短弹层（相当于 WinUI 的 Resize），让顶边仍落在控件位置；
        /// 只靠 Slide 约束会把整层往上推、选中项被推到控件之外。
        /// </summary>
        private void CapHeightToScreen(ComboBox combo, Control container)
        {
            if (_surface is null || !TryGetScreenBounds(combo, out var screenTop, out var screenBottom))
            {
                return;
            }

            var itemTop = container.TranslatePoint(default, _surface)?.Y ?? 0;
            var scale = TopLevel.GetTopLevel(_surface)?.RenderScaling ?? 1.0;
            if (scale <= 0)
            {
                scale = 1.0;
            }

            var comboTop = combo.PointToScreen(default).Y / scale;
            var available = screenBottom - (comboTop - itemTop);

            // 剩不到两项就别缩了，交给 Slide 约束处理。
            if (available < container.Bounds.Height * 2)
            {
                return;
            }

            var cap = Math.Min(combo.MaxDropDownHeight, available);
            if (cap >= _surface.Bounds.Height - 0.5 || _surface.Bounds.Height <= 0)
            {
                return;
            }

            _surface.MaxHeight = cap;
            _surface.UpdateLayout();
        }

        private bool TryGetScreenBounds(ComboBox combo, out double screenTop, out double screenBottom)
        {
            screenTop = 0;
            screenBottom = 0;

            var topLevel = TopLevel.GetTopLevel(_surface);
            if (_popup.IsUsingOverlayLayer && topLevel is not null &&
                _surface?.FindAncestorOfType<OverlayPopupHost>()?.GetVisualParent() is { } overlay)
            {
                // Overlay 的约束区域是窗口内的安全区域，不是整块显示器。
                var safeArea = new Rect(overlay.Bounds.Size)
                    .Deflate(topLevel.InsetsManager?.SafeAreaPadding ?? default);
                var overlayScale = topLevel.RenderScaling > 0 ? topLevel.RenderScaling : 1.0;
                screenTop = overlay.PointToScreen(safeArea.TopLeft).Y / overlayScale;
                screenBottom = overlay.PointToScreen(safeArea.BottomRight).Y / overlayScale;
                return true;
            }

            var screens = topLevel?.Screens;
            if (screens is null)
            {
                return false;
            }

            var scale = topLevel!.RenderScaling;
            if (scale <= 0)
            {
                scale = 1.0;
            }

            var screen = screens.ScreenFromPoint(combo.PointToScreen(default)) ?? screens.Primary;
            if (screen is null)
            {
                return false;
            }

            screenTop = screen.WorkingArea.Y / scale;
            screenBottom = (screen.WorkingArea.Y + screen.WorkingArea.Height) / scale;
            return true;
        }

        private bool TryBringSelectedItemIntoRange(ComboBox combo, double itemTop, double itemHeight, double surfaceHeight)
        {
            _scroll ??= _surface?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (_scroll is null || !TryGetScreenBounds(combo, out var screenTop, out var screenBottom))
            {
                return false;
            }

            var topLevel = TopLevel.GetTopLevel(_surface!)!;
            var scale = topLevel.RenderScaling;
            if (scale <= 0)
            {
                scale = 1.0;
            }

            var comboTop = combo.PointToScreen(default).Y / scale;

            // 选中项在视口里的可用区间：
            // 上界 = 弹层顶边不越过屏幕上沿 且 选中项自身不越出视口下沿；
            // 下界 = 弹层底边不越过屏幕下沿 且 选中项不越出视口上沿。
            var upper = Math.Min(comboTop - screenTop, surfaceHeight - itemHeight);
            var lower = Math.Max(comboTop + surfaceHeight - screenBottom, 0);
            if (upper <= lower)
            {
                return false;
            }

            var target = Math.Clamp(itemTop, lower, upper);
            if (Math.Abs(target - itemTop) < 0.5)
            {
                return false;
            }

            var maxOffset = Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height);
            var offset = Math.Clamp(_scroll.Offset.Y + (itemTop - target), 0, maxOffset);
            if (Math.Abs(offset - _scroll.Offset.Y) < 0.5)
            {
                return false;
            }

            _scroll.Offset = new Vector(_scroll.Offset.X, offset);
            return true;
        }

        /// <summary>把选中项平移到控件位置，返回它在弹出层内的垂直中心（展开原点）。</summary>
        private double AlignToSelectedItem(ComboBox combo, Control container, double itemTop)
        {
            if (_surface is null)
            {
                return double.NaN;
            }

            if (_popup.IsUsingOverlayLayer && _popup.PlacementTarget is { } target &&
                _popup.Child is { } child &&
                target.TranslatePoint(default, combo) is { } targetOrigin &&
                _surface.TranslatePoint(default, child) is { } surfaceOriginInChild)
            {
                // 此模板固定使用 Bottom：锚点是 PlacementTarget 左下角。
                // OverlayPopupHost 在渲染阶段才应用 Canvas.Left/Top，Opened/UpdateLayout
                // 后测到的屏幕坐标仍可能是 (0,0) 处的旧位置，因此直接求目标偏移。
                // Avalonia 12 定位时会排除 Popup.Child.Margin（模板的阴影留白）。
                _popup.HorizontalOffset = -targetOrigin.X - surfaceOriginInChild.X;
                _popup.VerticalOffset = combo.Bounds.Height / 2 - targetOrigin.Y - target.Bounds.Height
                    - surfaceOriginInChild.Y - itemTop - container.Bounds.Height / 2;
                return itemTop + container.Bounds.Height / 2;
            }

            var scale = TopLevel.GetTopLevel(_surface)?.RenderScaling ?? 1.0;
            if (scale <= 0)
            {
                scale = 1.0;
            }

            var comboCenter = combo.PointToScreen(new Point(combo.Bounds.Width / 2, combo.Bounds.Height / 2));
            var itemCenter = container.PointToScreen(new Point(container.Bounds.Width / 2, container.Bounds.Height / 2));
            var comboOrigin = combo.PointToScreen(default);
            var surfaceOrigin = _surface.PointToScreen(default);

            _popup.VerticalOffset -= (itemCenter.Y - comboCenter.Y) / scale;
            _popup.HorizontalOffset -= (surfaceOrigin.X - comboOrigin.X) / scale;

            return itemTop + container.Bounds.Height / 2;
        }

        private void Prepare(double anchorY)
        {
            if (_prepared || _surface is null)
            {
                return;
            }

            _prepared = true;

            _surface.RevealAnchorY = anchorY;
            _surface.RevealProgress = 0;
            if (_shadow is not null)
            {
                _shadow.Opacity = 0;
            }

            _clock.Reset();
            _clockStarted = false;

            // 关键：UI 线程静息时会睡 ~79ms，15ms 的 DispatcherTimer 唤不醒它（见 UiFramePacer 注释），
            // 逐帧动画必须自己持帧泵，并按渲染帧推进而不是按定时器推进。
            AcquirePacer();
            NextFrame();
        }

        private void NextFrame()
        {
            if (_framePending)
            {
                return;
            }

            var topLevel = TopLevel.GetTopLevel(_surface);
            if (topLevel is null)
            {
                // 拿不到 TopLevel 就没有帧回调，直接显示到底，别留一个永远不可见的弹层。
                if (_surface is not null) _surface.RevealProgress = 1;
                ReleasePacer();
                return;
            }

            _framePending = true;
            topLevel.RequestAnimationFrame(OnFrame);
        }

        private void OnFrame(TimeSpan timestamp)
        {
            _framePending = false;

            if (_surface is null || !_popup.IsOpen)
            {
                ReleasePacer();
                return;
            }

            // 首次打开要等弹层窗口创建（实测 ~120ms），期间没有渲染帧。
            // 计时从**第一帧**开始，否则动画会在露面时已经走完一半。
            if (!_clockStarted)
            {
                _clockStarted = true;
                _clock.Restart();
            }

            var ms = _clock.Elapsed.TotalMilliseconds;

            var reveal = Math.Clamp(ms / RevealMs, 0, 1);
            _surface.RevealProgress = EaseOutCubic(reveal);

            var shadow = Math.Clamp((ms - ShadowDelayMs) / ShadowMs, 0, 1);
            if (_shadow is not null)
            {
                _shadow.Opacity = EaseOutQuad(shadow);
            }

            if (reveal >= 1 && shadow >= 1)
            {
                ReleasePacer();
                return;
            }

            NextFrame();
        }

        private void AcquirePacer()
        {
            if (_pacerHeld)
            {
                return;
            }

            _pacerHeld = true;
            UiFramePacer.Acquire();
        }

        private void ReleasePacer()
        {
            if (!_pacerHeld)
            {
                return;
            }

            _pacerHeld = false;
            UiFramePacer.Release();
        }

        private void ResetVisuals()
        {
            if (_surface is not null)
            {
                _surface.RevealProgress = 0;
                _surface.RevealAnchorY = double.NaN;
            }

            if (_shadow is not null)
            {
                _shadow.Opacity = 0;
            }
        }

        private static double EaseOutCubic(double t) => 1 - Math.Pow(1 - t, 3);

        private static double EaseOutQuad(double t) => 1 - (1 - t) * (1 - t);
    }
}
