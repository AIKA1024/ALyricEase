using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Logging;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ALyricEase.Controls;

namespace ALyricEase.Headless;

/// <summary>菜单打开动画探针(--menuanim):验证 FlyoutOpenAnimation(合成动画)的方向规则与真实启动。
/// 1) 纯函数 ComputeStartOffset 六条方向规则断言(下开/上开/右侧子菜单/左翻转/指针下开/指针上开);
/// 2) 真实打开普通 Flyout 与 MenuFlyout(下方开、Placement=Top 上方开)、展开子菜单、关闭重开,
///    通过 Started 诊断回报每次实际启动的起始偏移(合成动画值不可读,以此验证)。</summary>
public static class MenuAnimProbe
{
    private sealed class CapturingFlyout : Flyout
    {
        public FlyoutPresenter? Presenter { get; private set; }

        protected override Control CreatePresenter()
        {
            Presenter = (FlyoutPresenter)base.CreatePresenter();
            return Presenter;
        }
    }

    private sealed class CapturingMenuFlyout : MenuFlyout
    {
        public MenuFlyoutPresenter? Presenter { get; private set; }

        protected override Control CreatePresenter()
        {
            Presenter = (MenuFlyoutPresenter)base.CreatePresenter();
            return Presenter;
        }
    }

    private static int _failCount;

    public static void Run()
    {
        // ---- 0. 入场偏移:整个弹层从裁剪区外滑入,首帧不再直接露出一半 ----
        AssertOffset("极短弹层(比 50 还矮,整体滑入)", 40, 40);
        AssertOffset("每日推荐 歌手/专辑(2 项 ~90px)", 90, 90);
        AssertOffset("侧栏歌单右键(3 项 ~130px)", 130, 130);
        AssertOffset("播放条歌曲菜单(10 项 ~300px)", 300, 300);
        AssertOffset("超长弹层", 900, 900);
        AssertOffset("高度不可用时的兜底", 0, 50);

        // ---- 1. 方向规则纯函数断言(仅纵向,与原版 MenuPopupThemeTransition 的 Top/Bottom 一致) ----
        AssertDir("下方打开", new PixelRect(100, 200, 200, 100), new PixelRect(90, 90, 220, 100), 0, -100);
        AssertDir("上方打开(下方空间不足翻转)", new PixelRect(100, 0, 200, 100), new PixelRect(90, 150, 220, 100), 0, 100);
        AssertDir("右侧子菜单(顶部与父项对齐)向下滑", new PixelRect(320, 50, 200, 200), new PixelRect(100, 50, 200, 200), 0, -200);
        AssertDir("左侧翻转子菜单(顶部对齐)向下滑", new PixelRect(0, 50, 200, 200), new PixelRect(210, 50, 200, 200), 0, -200);
        AssertDir("子菜单被屏幕底部顶起(下端点在目标内)向上滑", new PixelRect(320, -50, 200, 140), new PixelRect(100, 50, 200, 150), 0, 140);
        AssertDir("指针放置下开(上端点在目标内)", new PixelRect(50, 100, 200, 300), new PixelRect(40, 90, 320, 40), 0, -300);
        AssertDir("指针放置上开(下端点在目标内)", new PixelRect(50, 0, 200, 120), new PixelRect(40, 100, 320, 40), 0, 120);

        // ---- 2. 真实打开诊断 ----
        var flyout = new CapturingMenuFlyout { Placement = PlacementMode.Bottom };
        flyout.Items.Add(new MenuItem { Header = "重命名歌单" });
        var sub = new MenuItem { Header = "排序方式" };
        sub.Items.Add(new MenuItem { Header = "按名称" });
        flyout.Items.Add(sub);

        var button = new Button { Content = "更多操作" };
        var panel = new StackPanel { Margin = new Thickness(40), Children = { button } };
        var win = new Window
        {
            Width = 320,
            Height = 220,
            Content = panel,
        };
        win.Show();
        Dispatcher.UIThread.RunJobs();

        var starts = new List<(Control Surface, double Dx, double Dy)>();
        var primes = new List<(Control Surface, double Opacity)>();
        var startBaseOpacities = new List<(Control Surface, double Opacity)>();
        FlyoutOpenAnimation.Primed += OnPrimed;
        FlyoutOpenAnimation.Started += OnStarted;
        void OnPrimed(Visual surface) => primes.Add(((Control)surface, surface.Opacity));
        void OnStarted(Visual surface, double dx, double dy)
        {
            starts.Add(((Control)surface, dx, dy));
            startBaseOpacities.Add(((Control)surface, surface.Opacity));
        }

        // 普通 Flyout(音量/歌词字号同类):全局 FlyoutPresenter 样式必须启动同款淡入+滑入。
        var slider = new Slider
        {
            Width = 200,
            Minimum = 0,
            Maximum = 100,
            Value = 50,
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            TickPlacement = TickPlacement.Outside,
            TickFrequency = 20,
        };
        ControlTheme? sliderControlTheme = null;
        if (Application.Current?.Styles.TryGetResource("WinUISliderTheme", null, out var sliderTheme) == true
            && sliderTheme is ControlTheme controlTheme)
        {
            sliderControlTheme = controlTheme;
            slider.Theme = controlTheme;
        }
        else
        {
            Console.WriteLine("[menuanim] FAIL: 未找到 WinUISliderTheme");
            _failCount++;
        }

        var volumeText = new TextBlock { Text = "50", Width = 28, TextAlignment = Avalonia.Media.TextAlignment.Right };
        var ordinary = new CapturingFlyout
        {
            Placement = PlacementMode.Top,
            Content = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 10,
                Children = { new TextBlock { Text = "音量" }, slider, volumeText },
            },
        };
        ordinary.ShowAt(button);
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
        ReportStart("普通 Flyout", starts.LastOrDefault(), ordinary.Presenter, resolvedExpected: true);
        AssertStableSliderTrack(slider, volumeText);
        ordinary.Hide();
        Dispatcher.UIThread.RunJobs();

        // PlayerBar 的垂直音量结构：确认数字宽度变化不会带动弹层/Slider，且底轨下端保持固定。
        var verticalSlider = new Slider
        {
            Theme = sliderControlTheme,
            Height = 150,
            Minimum = 0,
            Maximum = 100,
            Value = 50,
            Orientation = Avalonia.Layout.Orientation.Vertical,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            TickPlacement = TickPlacement.Outside,
            TickFrequency = 20,
        };
        var verticalVolumeText = new TextBlock { Text = "50", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };
        var verticalFlyout = new CapturingFlyout
        {
            Placement = PlacementMode.Top,
            Content = new StackPanel
            {
                Spacing = 8,
                Margin = new Thickness(4),
                Children = { verticalSlider, verticalVolumeText },
            },
        };
        verticalFlyout.ShowAt(button);
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
        AssertStableVerticalSliderTrack(verticalSlider, verticalVolumeText, verticalFlyout.Presenter);
        verticalFlyout.Hide();
        Dispatcher.UIThread.RunJobs();

        // 下方打开:期望动画启动且矩形/目标解析成功(headless 无定位器,方向绝对值由纯函数断言覆盖)
        flyout.ShowAt(button);
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
        ReportStart("一级(下方打开)", starts.LastOrDefault(), flyout.Presenter, resolvedExpected: true);

        // 长菜单(播放条歌曲菜单同类):起始偏移必须按真实弹窗高度放大,而不是退回兜底 50。
        // 弹窗定位时表面可能还没 Arrange(Bounds=0),因此高度必须取自宿主窗口;
        // 真正起播则要等 Popup.Opened。下面同时守住完整高偏移与可见宿主两个条件。
        var tallCountBefore = starts.Count;
        var tall = new CapturingMenuFlyout { Placement = PlacementMode.Bottom };
        for (var i = 0; i < 10; i++)
        {
            tall.Items.Add(new MenuItem { Header = $"菜单项 {i + 1}" });
        }

        tall.ShowAt(button);
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
        var tallStart = starts.Skip(tallCountBefore).LastOrDefault();
        var tallExpected = ALyricEase.Controls.FlyoutOpenAnimation.ComputeEntranceOffset(
            tall.Presenter?.Bounds.Height ?? 0);
        var tallHostVisible = TopLevel.GetTopLevel(tallStart.Surface)?.IsVisible == true;
        var tallOk = ReferenceEquals(tallStart.Surface, tall.Presenter)
                     && Math.Abs(Math.Abs(tallStart.Dy) - tallExpected) < 0.5
                     && tallHostVisible;
        Console.WriteLine($"[menuanim] 长菜单实际偏移: |dy|={Math.Abs(tallStart.Dy):F0} 期望 {tallExpected:F0} " +
            $"(表面高 {tall.Presenter?.Bounds.Height:F0},宿主已可见={tallHostVisible}) {(tallOk ? "OK" : "FAIL")}");
        if (!tallOk)
        {
            _failCount++;
        }

        tall.Hide();
        Dispatcher.UIThread.RunJobs();

        // 子菜单:纵向滑入(方向绝对值由纯函数断言覆盖,此处仅验证启动与解析)
        var subCountBefore = starts.Count;
        sub.IsSubMenuOpen = true;
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
        var subBorder = sub.GetVisualDescendants().OfType<Popup>().FirstOrDefault()?.Child as Border;
        ReportStart("子菜单", starts.Skip(subCountBefore).LastOrDefault(), subBorder, resolvedExpected: true);

        // 重开重放:关闭→重开,应再次启动
        sub.IsSubMenuOpen = false;
        flyout.Hide();
        Dispatcher.UIThread.RunJobs();
        var countBeforeReopen = starts.Count;
        flyout.ShowAt(button);
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
        var reopened = starts.Count > countBeforeReopen;
        Console.WriteLine($"[menuanim] 一级 重开后动画重启: {reopened} (期望 True,每次打开重放)");
        if (!reopened)
        {
            _failCount++;
        }

        // 上方打开:Placement=Top,期望 (0,50) 向上滑(等价于下方空间不足翻转后的方向路径)
        flyout.Placement = PlacementMode.Top;
        flyout.Hide();
        Dispatcher.UIThread.RunJobs();
        var topCountBefore = starts.Count;
        flyout.ShowAt(button);
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
        ReportStart("一级(Placement=Top 上方打开)", starts.Skip(topCountBefore).LastOrDefault(), flyout.Presenter, resolvedExpected: true);

        // 强制走内联 OverlayPopupHost，覆盖 Android 没有独立 PopupRoot 的打开路径。
        var overlaySurface = new Border
        {
            Width = 160,
            Height = 96,
            Background = Avalonia.Media.Brushes.DarkGray,
            Child = new TextBlock { Text = "Android overlay" },
        };
        FlyoutOpenAnimation.SetIsEnabled(overlaySurface, true);
        var overlayPopup = new Popup
        {
            Child = overlaySurface,
            PlacementTarget = button,
            Placement = PlacementMode.Bottom,
            ShouldUseOverlayLayer = true,
        };
        panel.Children.Add(overlayPopup);
        var overlayStartCount = starts.Count;
        overlayPopup.IsOpen = true;
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
        var overlayStarted = starts.Skip(overlayStartCount).Any(x => ReferenceEquals(x.Surface, overlaySurface));
        var usesOverlayHost = overlaySurface.GetVisualAncestors().OfType<OverlayPopupHost>().Any();
        var overlayPrimed = primes.Any(x => ReferenceEquals(x.Surface, overlaySurface) && Math.Abs(x.Opacity) < 0.001);
        Console.WriteLine($"[menuanim] Android overlay: 宿主={usesOverlayHost} 首帧透明={overlayPrimed} " +
                          $"动画启动={overlayStarted}");
        if (!usesOverlayHost || !overlayPrimed || !overlayStarted)
        {
            _failCount++;
        }
        overlayPopup.IsOpen = false;
        Dispatcher.UIThread.RunJobs();

        FlyoutOpenAnimation.Primed -= OnPrimed;
        FlyoutOpenAnimation.Started -= OnStarted;

        // Android overlay 的关键时序：表面必须在 attach 前以 Opacity=0 预备，启动动画时基值已恢复。
        var allPrimed = starts.All(start => primes.Any(prime =>
            ReferenceEquals(prime.Surface, start.Surface) && Math.Abs(prime.Opacity) < 0.001));
        var allRestored = startBaseOpacities.All(start =>
            Math.Abs(start.Opacity - 1f) < 0.001);
        Console.WriteLine($"[menuanim] 首帧透明预备={allPrimed} 动画启动时基值恢复={allRestored} " +
                          $"(预备 {primes.Count} 次/启动 {starts.Count} 次)");
        if (!allPrimed || !allRestored)
        {
            _failCount++;
        }

        // 合成视觉可用性(headless 下也应非空)
        var hasVisual = flyout.Presenter is not null && ElementComposition.GetElementVisual(flyout.Presenter) is not null;
        Console.WriteLine($"[menuanim] 合成视觉可用: {hasVisual} (期望 True)");
        if (!hasVisual)
        {
            _failCount++;
        }

        // 静止观感 PNG(动画结束态与样式无副作用核对)
        var px = new PixelSize((int)win.ClientSize.Width, (int)win.ClientSize.Height);
        using var rtb = new RenderTargetBitmap(px, new Vector(96, 96));
        rtb.Render(win);
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tmpandroid", "menuanim.png");
        rtb.Save(System.IO.Path.GetFullPath(path));
        Console.WriteLine($"[menuanim] 主窗口截图: {System.IO.Path.GetFullPath(path)}");

        Console.WriteLine(_failCount == 0 ? "[menuanim] 全部断言通过" : $"[menuanim] 失败断言数: {_failCount}");
        Environment.ExitCode = _failCount == 0 ? 0 : 1;
    }

    private static void ReportStart(string label, (Control Surface, double Dx, double Dy) actual,
        Control? expectedSurface, bool resolvedExpected)
    {
        // headless 无弹窗定位器,弹窗矩形为 (0,0) 系(方向绝对值仅桌面有效),故只断言:
        // 1) 目标表面的动画确实启动 2) 弹窗矩形与放置目标解析成功
        var resolved = FlyoutOpenAnimation.LastResolved;
        var hostVisibleAtStart = TopLevel.GetTopLevel(actual.Surface)?.IsVisible == true;
        var ok = expectedSurface is not null && ReferenceEquals(actual.Surface, expectedSurface)
                 && resolved == (resolvedExpected, resolvedExpected) && hostVisibleAtStart;
        Console.WriteLine($"[menuanim] {label}: 启动偏移=({actual.Dx:F0},{actual.Dy:F0}) " +
            $"矩形/目标解析={resolved} (期望 {resolvedExpected}) " +
            $"宿主已可见={hostVisibleAtStart} 表面正确={ReferenceEquals(actual.Surface, expectedSurface)} {(ok ? "OK" : "FAIL")}");
        if (!ok)
        {
            _failCount++;
        }
    }

    private static void AssertStableSliderTrack(Slider slider, TextBlock volumeText)
    {
        var baseTrack = slider.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(x => x.Name == "TrackBaseBackground");
        if (baseTrack is null)
        {
            Console.WriteLine("[menuanim] FAIL: 横向音量滑块没有固定底轨");
            _failCount++;
            return;
        }

        var rightEdges = new List<double>();
        foreach (var value in new[] { 0d, 1d, 9d, 10d, 11d, 50d, 99d, 100d })
        {
            slider.Value = value;
            volumeText.Text = value.ToString("0");
            Dispatcher.UIThread.RunJobs();
            var right = baseTrack.TranslatePoint(new Point(baseTrack.Bounds.Width, 0), slider)?.X ?? double.NaN;
            rightEdges.Add(right);
        }

        var stable = rightEdges.All(x => double.IsFinite(x) && Math.Abs(x - rightEdges[0]) < 0.01);
        Console.WriteLine($"[menuanim] 音量底轨右端稳定: {stable} " +
                          $"范围={rightEdges.Min():F2}..{rightEdges.Max():F2} TextWidth={volumeText.Bounds.Width:F0}");
        if (!stable || Math.Abs(volumeText.Bounds.Width - 28) > 0.01)
        {
            _failCount++;
        }
    }

    private static void AssertStableVerticalSliderTrack(Slider slider, TextBlock volumeText, FlyoutPresenter? presenter)
    {
        var baseTrack = slider.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(x => x.Name == "TrackBaseBackground");
        var decrease = slider.GetVisualDescendants().OfType<RepeatButton>()
            .FirstOrDefault(x => x.Name == "PART_DecreaseButton");
        if (baseTrack is null || decrease is null || presenter is null)
        {
            Console.WriteLine("[menuanim] FAIL: 垂直音量滑块诊断元素不完整");
            _failCount++;
            return;
        }

        var baseBottoms = new List<double>();
        var fillBottoms = new List<double>();
        var sliderLefts = new List<double>();
        var presenterWidths = new List<double>();
        foreach (var value in new[] { 0d, 1d, 9d, 10d, 11d, 50d, 99d, 100d })
        {
            slider.Value = value;
            volumeText.Text = value.ToString("0");
            Dispatcher.UIThread.RunJobs();
            baseBottoms.Add(baseTrack.TranslatePoint(new Point(0, baseTrack.Bounds.Height), slider)?.Y ?? double.NaN);
            fillBottoms.Add(decrease.TranslatePoint(new Point(0, decrease.Bounds.Height), slider)?.Y ?? double.NaN);
            sliderLefts.Add(slider.TranslatePoint(default, presenter)?.X ?? double.NaN);
            presenterWidths.Add(presenter.Bounds.Width);
        }

        static bool Stable(IReadOnlyList<double> values) =>
            values.All(x => double.IsFinite(x) && Math.Abs(x - values[0]) < 0.01);

        var stable = Stable(baseBottoms) && Stable(fillBottoms) && Stable(sliderLefts) && Stable(presenterWidths);
        Console.WriteLine($"[menuanim] 垂直音量布局稳定: {stable} " +
                          $"底轨={baseBottoms.Min():F2}..{baseBottoms.Max():F2} " +
                          $"填充端={fillBottoms.Min():F2}..{fillBottoms.Max():F2} " +
                          $"SliderX={sliderLefts.Min():F2}..{sliderLefts.Max():F2} " +
                          $"FlyoutW={presenterWidths.Min():F2}..{presenterWidths.Max():F2} " +
                          $"TextW={volumeText.Bounds.Width:F2}");
        if (!stable)
        {
            _failCount++;
        }
    }

    private static void AssertDir(string label, PixelRect popup, PixelRect target, double expectedDx, double expectedDy)
    {
        // 探针里的矩形是物理像素,scale=1 时与 DIP 数值相同,直接把弹窗高度当表面高度传入
        var (dx, dy) = FlyoutOpenAnimation.ComputeStartOffset(popup, target, popup.Height);
        var ok = Math.Abs(dx - expectedDx) < 0.5 && Math.Abs(dy - expectedDy) < 0.5;
        Console.WriteLine($"[menuanim] 规则[{label}]: ({dx:F0},{dy:F0}) 期望 ({expectedDx:F0},{expectedDy:F0}) {(ok ? "OK" : "FAIL")}");
        if (!ok)
        {
            _failCount++;
        }
    }

    private static void AssertOffset(string label, double surfaceHeight, double expected)
    {
        var actual = FlyoutOpenAnimation.ComputeEntranceOffset(surfaceHeight);
        var ok = Math.Abs(actual - expected) < 0.5;
        Console.WriteLine($"[menuanim] 偏移[{label}]: {actual:F0} 期望 {expected:F0} {(ok ? "OK" : "FAIL")}");
        if (!ok)
        {
            _failCount++;
        }
    }
}
