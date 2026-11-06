using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ALyricEase.Controls;
using ALyricEase.Infrastructure;
using ALyricEase.ViewModels;
using ALyricEase.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ALyricEase.Headless;

/// <summary>
/// 海报位左下角的"自动切图倒计时"(--hero-ring):真窗口 + 真数据。
/// 验四件事:①位置在二维码同款左下角、不挡正文;②进度会自己走;
/// ③指针压在海报上真的暂停(<b>走的是 XAML 上的 PointerEntered/Moved/Exited</b>,不是直接改属性)、移开继续;
/// ④一圈走完自己切下一张,且出现/消失是透明度过渡(不是硬显隐)。
/// </summary>
internal static class HeroRingProbe
{
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(90);
    private static int _failures;

    public static async Task<int> RunAsync()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var root = Path.Combine(Path.GetTempPath(), "aly-hero-ring-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var vm = ServiceLocator.Get<BrowseViewModel>();
        _ = vm.EnsureLoadedAsync();
        await WaitUntilAsync(() => !vm.IsLoading && vm.HeroItems.Count > 1, LoadTimeout,
            "海报位加载超时(需要至少 2 张榜单)").ConfigureAwait(true);

        var view = new BrowseView { DataContext = vm };
        var window = new Window { Width = 1120, Height = 900, Content = view };
        window.Bind(Window.BackgroundProperty, new DynamicResourceExtension("AppBackground"));
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        window.Show();
        await SettleAsync(1500).ConfigureAwait(true);

        var ring = view.GetVisualDescendants().OfType<HeroAdvanceRing>().FirstOrDefault();
        var frame = view.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(border => border.Classes.Contains("hero-frame"));
        if (ring is null || frame is null)
        {
            Console.WriteLine("[hero-ring] FAIL 找不到 HeroAdvanceRing / hero-frame");
            return 1;
        }

        Console.WriteLine($"[hero-ring] 海报 {vm.HeroItems.Count} 张;圆 尺寸={ring.Bounds.Width:0}×{ring.Bounds.Height:0} " +
                          $"不透明度={ring.Opacity:0.00} 可命中={ring.IsHitTestVisible}");

        // ① 淡入完成 + 贴在左下角(不挡正文/按钮)
        Check(ring.Opacity > 0.98, $"圆应当已经淡入(实际 {ring.Opacity:0.00})");
        Check(!ring.IsHitTestVisible, "圆不能吃鼠标事件(否则会挡掉海报的点击/悬停)");
        Check(ring.Bounds.Width is >= 14 and <= 21, $"圆的尺寸应在 14..21(实际 {ring.Bounds.Width:0},太大就抢正文了)");
        var at = ring.TranslatePoint(new Point(0, 0), frame) ?? new Point(-1, -1);
        var insetLeft = at.X;
        var insetBottom = frame.Bounds.Height - (at.Y + ring.Bounds.Height);
        Console.WriteLine($"[hero-ring] 相对海报:左边距 {insetLeft:0}px 底边距 {insetBottom:0}px");
        Check(insetLeft is >= 8 and <= 24, $"左边距应在 8..24(实际 {insetLeft:0})");
        Check(insetBottom is >= 6 and <= 24, $"底边距应在 6..24(实际 {insetBottom:0})");

        // ② 进度自己走
        var before = ring.Progress;
        await SettleAsync(900).ConfigureAwait(true);
        var after = ring.Progress;
        Console.WriteLine($"[hero-ring] 进度 {before:0.00} → {after:0.00}(0.9s)");
        Check(after > before, $"进度应当前进(实际 {before:0.00} → {after:0.00})");

        // ③ 指针压在海报上 → 暂停;移开 → 继续
        var center = frame.TranslatePoint(new Point(frame.Bounds.Width / 2, frame.Bounds.Height / 2), window)
                     ?? new Point(400, 200);
        window.MouseMove(center);
        await SettleAsync(250).ConfigureAwait(true);
        Check(ring.IsPaused, "指针在海报上时圆应当暂停");
        var frozen = ring.Progress;
        await SettleAsync(800).ConfigureAwait(true);
        Console.WriteLine($"[hero-ring] 悬停 0.8s:进度 {frozen:0.00} → {ring.Progress:0.00}");
        Check(Math.Abs(ring.Progress - frozen) < 0.02, "暂停期间进度不能动");

        window.MouseMove(new Point(8, 880));
        await SettleAsync(250).ConfigureAwait(true);
        Check(!ring.IsPaused, "指针离开海报后应恢复");
        var resumed = ring.Progress;
        await SettleAsync(700).ConfigureAwait(true);
        Console.WriteLine($"[hero-ring] 移开 0.7s:进度 {resumed:0.00} → {ring.Progress:0.00}");
        Check(ring.Progress > resumed, "恢复后进度应当继续走");

        // ④ 一圈走完自己切下一张(把时长缩短,别真等 6 秒)
        var indexBefore = vm.HeroIndex;
        var durationBefore = ring.Duration;
        ring.Duration = TimeSpan.FromMilliseconds(1200);
        ring.Restart();
        await SettleAsync(900).ConfigureAwait(true); // 先过掉 Restart 自己那次淡出(620ms 等待 + 160ms 淡入)
        Check(ring.Opacity > 0.9, $"Restart 之后应当淡回来(实际 {ring.Opacity:0.00})");

        var minOpacity = 1d;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (vm.HeroIndex == indexBefore && DateTime.UtcNow < deadline)
        {
            await Task.Delay(40).ConfigureAwait(true);
            Dispatcher.UIThread.RunJobs();
        }
        // 切图瞬间应当看到"先淡出"的中间值;直接归零会是硬切(不透明度一直是 1)。
        // 同时钉住:整段翻页里进度**一格都不许走** —— 圆虽然看不见,表要一起停,
        // 否则淡回来时已经走了十分之一,看着像"切图时圈还在跑"。
        var progressAtSwitch = ring.Progress;
        var progressDuringSwitch = progressAtSwitch;
        for (var i = 0; i < 13; i++) // ~390ms,短于 ResumeDelay(620ms)
        {
            await Task.Delay(30).ConfigureAwait(true);
            Dispatcher.UIThread.RunJobs();
            minOpacity = Math.Min(minOpacity, ring.Opacity);
            progressDuringSwitch = Math.Max(progressDuringSwitch, ring.Progress);
        }
        ring.Duration = durationBefore;
        Check(vm.HeroIndex != indexBefore,
            $"走满一圈应切到下一张(实际停在 {vm.HeroIndex},原本 {indexBefore})");
        Console.WriteLine($"[hero-ring] 自动切图 {indexBefore} → {vm.HeroIndex};" +
                          $"切图期间最低不透明度 {minOpacity:0.00} 进度 {progressAtSwitch:0.000}→{progressDuringSwitch:0.000}");
        Check(minOpacity < 0.5, $"切图时圆要先淡出(最低读到 {minOpacity:0.00}),而不是硬清零");
        Check(progressDuringSwitch - progressAtSwitch < 0.02,
            $"切图期间进度必须冻住(实际 {progressAtSwitch:0.000} → {progressDuringSwitch:0.000})");
        await SettleAsync(1000).ConfigureAwait(true);
        Check(ring.Opacity > 0.98, $"翻页落定后圆应当淡回来(实际 {ring.Opacity:0.00})");
        Check(ring.Progress > 0.02, $"淡回来之后进度应当重新开始走(实际 {ring.Progress:0.000})");

        // ⑤ 出现/消失是过渡,不是硬切:关掉后应看到中间值,再归零
        ring.IsActive = false;
        await SettleAsync(60).ConfigureAwait(true);
        var mid = ring.Opacity;
        await SettleAsync(400).ConfigureAwait(true);
        Console.WriteLine($"[hero-ring] 淡出中 {mid:0.00} → {ring.Opacity:0.00}");
        Check(ring.Opacity < 0.02, "关掉后应当完全透明");
        Check(mid is > 0.02 and < 0.98, $"应当在过渡途中(60ms 时读到 {mid:0.00}),否则是硬切");
        ring.IsActive = true;
        await SettleAsync(400).ConfigureAwait(true);
        Check(ring.Opacity > 0.98, "重新激活后应当淡回不透明");

        // ⑥ 翻页动画:先快后满 + 旧图慢慢变暗。⚠ 合成动画的值**读不回来** ——
        //    CompositionVisual.Translation/Opacity 的 getter 返回的是"基值"(我们起动画前写进去的终值),
        //    所以这里只能钉配置;曲线形状和变暗观感要在真窗口里看(--spinner-motion-real 的路子)。
        var carousel = view.GetVisualDescendants().OfType<Carousel>().FirstOrDefault();
        if (carousel?.PageTransition is not CompositionPageTransition transition)
        {
            Console.WriteLine("[hero-ring] FAIL 轮播没有用 CompositionPageTransition");
            _failures++;
        }
        else
        {
            Console.WriteLine($"[hero-ring] 翻页 时长={transition.Duration.TotalMilliseconds:0}ms " +
                              $"退场停在不透明度={transition.ExitOpacity:0.00} 强缓出={transition.StrongEaseOutCurve}");
            Check(Math.Abs(transition.Duration.TotalMilliseconds - 600) < 1, "翻页时长应为 600ms(商店那种节奏)");
            Check(transition.ExitOpacity is > 0.05 and < 0.5, "旧图退场应当停在半暗(不是完全透明)");
            Check(transition.StrongEaseOutCurve, "轮播要用起步更急的缓出曲线(先快后满)");
        }

        // ⑦ 滚到下面的内容时,轮播自己切图不能把页面拽回去:
        //    圆点 ListBox 换了选中项会 ScrollIntoView,那个请求会一路冒泡到整页的 ScrollViewer。
        var pageScroller = view.GetVisualDescendants().OfType<ScrollViewer>()
            .First(scroller => scroller.Classes.Contains("page-scroll"));
        pageScroller.Offset = new Vector(0, 600);
        await SettleAsync(200).ConfigureAwait(true);
        var offsetBefore = pageScroller.Offset.Y;
        vm.NextHeroCommand.Execute(null);
        await SettleAsync(900).ConfigureAwait(true);
        var offsetAfter = pageScroller.Offset.Y;
        Console.WriteLine($"[hero-ring] 页面滚动 {offsetBefore:0} → {offsetAfter:0}(切图一次)");
        Check(Math.Abs(offsetAfter - offsetBefore) < 2, "轮播切图不能把页面滚走");
        pageScroller.Offset = new Vector(0, 0);
        await SettleAsync(300).ConfigureAwait(true);

        // 截图:让圆停在 40% 左右,能看出"扇形长了一半"
        ring.Restart();
        await SettleAsync(2400).ConfigureAwait(true);
        Capture(window, root, "hero-ring-wide");

        window.Width = 620;
        await SettleAsync(700).ConfigureAwait(true);
        ring.Restart();
        await SettleAsync(2000).ConfigureAwait(true);
        Capture(window, root, "hero-ring-narrow");

        window.Close();
        Console.WriteLine(_failures == 0 ? "[hero-ring] PASS" : $"[hero-ring] FAIL {_failures} 项");
        Console.WriteLine("[hero-ring] capture " + root);
        return _failures == 0 ? 0 : 1;
    }

    private static void Check(bool condition, string message)
    {
        if (condition) return;
        _failures++;
        Console.WriteLine("[hero-ring] FAIL " + message);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string message)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(120).ConfigureAwait(true);
        }
        throw new TimeoutException(message);
    }

    private static async Task SettleAsync(int milliseconds)
    {
        for (var i = 0; i < milliseconds / 40; i++)
        {
            await Task.Delay(40).ConfigureAwait(true);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void Capture(Window window, string root, string name)
    {
        var bitmap = new RenderTargetBitmap(new PixelSize((int)window.ClientSize.Width,
            (int)window.ClientSize.Height), new Vector(96, 96));
        bitmap.Render(window);
        var path = Path.Combine(root, name + ".png");
        bitmap.Save(path);
        Console.WriteLine($"[hero-ring] 截图 {name} -> {path}");
    }
}
