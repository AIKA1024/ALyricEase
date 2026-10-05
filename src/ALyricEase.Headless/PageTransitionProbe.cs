using System.Diagnostics;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using ALyricEase.Infrastructure;

namespace ALyricEase.Headless;

/// <summary>入口: dotnet run --project src/ALyricEase.Headless -- --page-transition。
/// 用阻塞 UI 线程复现默认页面动画停帧，再检查过渡取消与双 presenter 复用。</summary>
internal static class PageTransitionProbe
{
    public static async Task RunAsync()
    {
        var from = new Border { Background = Brushes.Red };
        var to = new Border { Background = Brushes.Blue };
        var root = new Grid { Children = { from, to } };
        var window = new Window { Width = 640, Height = 360, Content = root };
        window.Show();
        window.UpdateLayout();
        using var cancellation = new CancellationTokenSource();
        var transition = new CompositePageTransition();
        transition.PageTransitions.Add(new PageSlide { Orientation = PageSlide.SlideAxis.Horizontal,
            Duration = TimeSpan.FromMilliseconds(300) });
        transition.PageTransitions.Add(new CrossFade(TimeSpan.FromMilliseconds(300)));
        var task = transition.Start(from, to, true, cancellation.Token);
        await Task.Delay(70);
        var before = to.Opacity;
        var sw = Stopwatch.StartNew();
        Thread.Sleep(180); // 模拟复杂页在 UI 线程建树/布局/更新集合。
        Console.WriteLine($"[page-transition] built-in: UI blocked={sw.Elapsed.TotalMilliseconds:F0}ms, "
            + $"opacity before={before:F3}, after={to.Opacity:F3}");
        if (Math.Abs(before - to.Opacity) > 0.0001)
            throw new InvalidOperationException("阻塞期间的动画值意外变化，复现场景无效。");
        await task;
        root.Children.Clear();
        from = new Border { Background = Brushes.Red };
        to = new Border { Background = Brushes.Blue };
        root.Children.Add(from);
        root.Children.Add(to);
        window.UpdateLayout();
        var composition = new CompositionPageTransition { Duration = TimeSpan.FromMilliseconds(150) };
        var outgoing = ElementComposition.GetElementVisual(from)!;
        var incoming = ElementComposition.GetElementVisual(to)!;
        var composed = composition.Start(from, to, true, CancellationToken.None);
        Check(outgoing.Translation.X == -root.Bounds.Width, "前进退场方向");
        Check(incoming.Translation.X == 0 && incoming.Opacity == 1, "入场末帧基值");
        Check(!from.IsHitTestVisible && !to.IsHitTestVisible, "平移期间禁止错位点击");
        Check(from.RenderTransform is null && to.RenderTransform is null
            && to.Opacity == 1, "不再逐帧修改 UI 属性");
        await composed;
        Check(!from.IsVisible && to.IsVisible, "正常完成的页面可见性");
        Check(from.IsHitTestVisible && to.IsHitTestVisible, "完成后恢复页面交互");
        Check(outgoing.Translation.X == 0 && outgoing.Opacity == 1, "完成后恢复 presenter 状态");

        from.IsVisible = true;
        using var firstCancellation = new CancellationTokenSource();
        var first = composition.Start(from, to, true, firstCancellation.Token);
        await Task.Delay(35);
        firstCancellation.Cancel();
        // 复用两个 presenter，同时反向返回。旧任务此时仍等着 UI 调度清理。
        var second = composition.Start(to, from, false, CancellationToken.None);
        await ExpectCancellation(first);
        Check(incoming.Translation.X == root.Bounds.Width && incoming.Opacity == 0,
            "过期回调未覆盖反向退场动画");
        Check(from.IsVisible && outgoing.Opacity == 1, "过期回调未隐藏重新入场页");
        Check(!from.IsHitTestVisible && !to.IsHitTestVisible, "过期回调未恢复新动画的交互");
        await second;
        Check(!to.IsVisible && from.IsVisible && incoming.Translation.X == 0,
            "返回完成后状态恢复");
        Check(from.IsHitTestVisible && to.IsHitTestVisible, "返回完成后恢复交互");

        using var snapCancellation = new CancellationTokenSource();
        var interrupted = composition.Start(from, to, true, snapCancellation.Token);
        snapCancellation.Cancel();
        composition.Duration = TimeSpan.Zero;
        await composition.Start(to, from, false, CancellationToken.None);
        await ExpectCancellation(interrupted);
        Check(from.IsVisible && !to.IsVisible && outgoing.Translation.X == 0
            && incoming.Opacity == 1, "动画中切换为零时长也清理状态");
        window.Close();

        // 使用真实 TransitioningContentControl，检查双 presenter 的内容释放与快速连跳。
        var host = new TransitioningContentControl { PageTransition = new CompositionPageTransition
            { Duration = TimeSpan.FromMilliseconds(80) } };
        var hostWindow = new Window { Width = 640, Height = 360, Content = host };
        var pages = Enumerable.Range(0, 7).Select(i => new Border
            { Child = new TextBlock { Text = $"page {i}" } }).ToArray();
        host.Content = pages[0];
        hostWindow.Show();
        hostWindow.UpdateLayout();
        for (var i = 1; i < pages.Length; i++)
        {
            host.IsTransitionReversed = i % 2 == 0;
            host.Content = pages[i];
            hostWindow.UpdateLayout();
            await Task.Delay(20);
        }
        await Task.Delay(220);
        var presenters = host.GetVisualDescendants().OfType<ContentPresenter>().ToArray();
        Check(presenters.Count(p => p.Content is Border) == 1, "仅保留最后一页内容");
        Check(pages[^1].IsAttachedToVisualTree()
            && pages.Take(pages.Length - 1).All(p => !p.IsAttachedToVisualTree()), "旧页面全部摘树");
        foreach (var presenter in presenters)
        {
            var visual = ElementComposition.GetElementVisual(presenter)!;
            Check(visual.Translation.X == 0 && visual.Opacity == 1, "双 presenter 无残留动画状态");
        }
        // 离树时取消过渡，任务仍应释放持有的视觉引用。
        using var detachCancellation = new CancellationTokenSource();
        var detachTransition = new CompositionPageTransition();
        var visiblePresenter = presenters.Single(p => p.Content is Border);
        var detachedTask = detachTransition.Start(null, visiblePresenter, true, detachCancellation.Token);
        hostWindow.Close();
        detachCancellation.Cancel();
        await ExpectCancellation(detachedTask);
        Console.WriteLine("[page-transition] PASS: forward/back, cancellation, presenter reuse, "
            + "zero duration, old page detach, window close");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task ExpectCancellation(Task task)
    {
        try { await task; throw new InvalidOperationException("预期任务被取消。"); }
        catch (OperationCanceledException) { }
    }

    public sealed class ProbeApp : Application
    {
        public override void Initialize() => Styles.Add(new FluentTheme());
    }
}
