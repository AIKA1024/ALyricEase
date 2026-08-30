using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Logging;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ALyricEase.Controls;

namespace ALyricEase.Headless;

/// <summary>菜单打开动画探针(--menuanim):验证 FlyoutOpenAnimation(合成动画)的方向规则与真实启动。
/// 1) 纯函数 ComputeStartOffset 六条方向规则断言(下开/上开/右侧子菜单/左翻转/指针下开/指针上开);
/// 2) 真实打开 MenuFlyout(下方开、Placement=Top 上方开)、展开子菜单、关闭重开,
///    通过 Started 诊断回报每次实际启动的起始偏移(合成动画值不可读,以此验证)。</summary>
public static class MenuAnimProbe
{
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
        // ---- 1. 方向规则纯函数断言(仅纵向,与原版 MenuPopupThemeTransition 的 Top/Bottom 一致) ----
        AssertDir("下方打开", new PixelRect(100, 200, 200, 100), new PixelRect(90, 90, 220, 100), 0, -50);
        AssertDir("上方打开(下方空间不足翻转)", new PixelRect(100, 0, 200, 100), new PixelRect(90, 150, 220, 100), 0, 50);
        AssertDir("右侧子菜单(顶部与父项对齐)向下滑", new PixelRect(320, 50, 200, 200), new PixelRect(100, 50, 200, 200), 0, -50);
        AssertDir("左侧翻转子菜单(顶部对齐)向下滑", new PixelRect(0, 50, 200, 200), new PixelRect(210, 50, 200, 200), 0, -50);
        AssertDir("子菜单被屏幕底部顶起(下端点在目标内)向上滑", new PixelRect(320, -50, 200, 140), new PixelRect(100, 50, 200, 150), 0, 50);
        AssertDir("指针放置下开(上端点在目标内)", new PixelRect(50, 100, 200, 300), new PixelRect(40, 90, 320, 40), 0, -50);
        AssertDir("指针放置上开(下端点在目标内)", new PixelRect(50, 0, 200, 120), new PixelRect(40, 100, 320, 40), 0, 50);

        // ---- 2. 真实打开诊断 ----
        var flyout = new CapturingMenuFlyout { Placement = PlacementMode.Bottom };
        flyout.Items.Add(new MenuItem { Header = "重命名歌单" });
        var sub = new MenuItem { Header = "排序方式" };
        sub.Items.Add(new MenuItem { Header = "按名称" });
        flyout.Items.Add(sub);

        var button = new Button { Content = "更多操作" };
        var win = new Window
        {
            Width = 320,
            Height = 220,
            Content = new StackPanel { Margin = new Thickness(40), Children = { button } },
        };
        win.Show();
        Dispatcher.UIThread.RunJobs();

        var starts = new List<(Control Surface, double Dx, double Dy)>();
        FlyoutOpenAnimation.Started += OnStarted;
        void OnStarted(Visual surface, double dx, double dy) => starts.Add(((Control)surface, dx, dy));

        // 下方打开:期望动画启动且矩形/目标解析成功(headless 无定位器,方向绝对值由纯函数断言覆盖)
        flyout.ShowAt(button);
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
        ReportStart("一级(下方打开)", starts.LastOrDefault(), flyout.Presenter, resolvedExpected: true);

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

        FlyoutOpenAnimation.Started -= OnStarted;

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
        var ok = expectedSurface is not null && ReferenceEquals(actual.Surface, expectedSurface)
                 && resolved == (resolvedExpected, resolvedExpected);
        Console.WriteLine($"[menuanim] {label}: 启动偏移=({actual.Dx:F0},{actual.Dy:F0}) " +
            $"矩形/目标解析={resolved} (期望 {resolvedExpected}) 表面正确={ReferenceEquals(actual.Surface, expectedSurface)} {(ok ? "OK" : "FAIL")}");
        if (!ok)
        {
            _failCount++;
        }
    }

    private static void AssertDir(string label, PixelRect popup, PixelRect target, double expectedDx, double expectedDy)
    {
        var (dx, dy) = FlyoutOpenAnimation.ComputeStartOffset(popup, target);
        var ok = Math.Abs(dx - expectedDx) < 0.5 && Math.Abs(dy - expectedDy) < 0.5;
        Console.WriteLine($"[menuanim] 规则[{label}]: ({dx:F0},{dy:F0}) 期望 ({expectedDx:F0},{expectedDy:F0}) {(ok ? "OK" : "FAIL")}");
        if (!ok)
        {
            _failCount++;
        }
    }
}
