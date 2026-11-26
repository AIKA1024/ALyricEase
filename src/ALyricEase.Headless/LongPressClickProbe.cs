using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ALyricEase.Infrastructure;

namespace ALyricEase.Headless;

/// <summary>触控长按的误点击护栏探针(--longpress-click):Avalonia 只在手势层区分 Holding(松手只发
/// RightTapped),Button.Click 不看手势层——长按期间 IsPressed 一直是 true,松手必然 OnClick。
/// 用无头触摸复现:未挂 <see cref="LongPressClickGuard"/> 的按钮长按松手会点一次(bug);
/// 挂了护栏的长按松手 0 次(修复生效)。</summary>
internal static class LongPressClickProbe
{
    public static async Task RunAsync()
    {
        var plain = await RunCaseAsync(guarded: false);
        var guarded = await RunCaseAsync(guarded: true);

        Check(plain.ContextCount == 1, $"未护栏:长按触发 ContextRequested(实际 {plain.ContextCount})");
        Check(plain.Clicks == 1, $"未护栏:松手仍触发 Click —— bug 复现(实际 {plain.Clicks})");
        Check(guarded.ContextCount == 1, $"加护栏:长按触发 ContextRequested(实际 {guarded.ContextCount})");
        Check(guarded.Clicks == 0, $"加护栏:松手不再触发 Click(实际 {guarded.Clicks})");

        Console.WriteLine("[longpress-click] PASS; 长按弹菜单后松手不再误触 Button.Click");
    }

    /// <summary>一次完整触摸手势:按下 → 按住超过 HoldWaitDuration → 抬起。返回菜单与点击次数。</summary>
    private static async Task<(int ContextCount, int Clicks)> RunCaseAsync(bool guarded)
    {
        var clicks = 0;
        var contexts = 0;
        var button = new Button
        {
            Content = guarded ? "guarded" : "plain",
            Width = 200,
            Height = 64,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        var root = new Grid { Children = { button } };
        var window = new Window { Width = 400, Height = 300, Content = root };
        MenuFlyout? flyout = null;

        if (guarded) LongPressClickGuard.Attach(root);
        button.Click += (_, _) => clicks++;
        button.AddHandler(InputElement.ContextRequestedEvent, (_, e) =>
        {
            contexts++;
            e.Handled = true;
            // 与真实入口一致:长按成立 → 先退出按下态 → 再弹菜单
            if (guarded) LongPressClickGuard.CancelPress(root);
            flyout = new MenuFlyout();
            flyout.Items.Add(new MenuItem { Header = "菜单项" });
            flyout.ShowAt(button);
        }, RoutingStrategies.Bubble);

        window.Show();
        Dispatcher.UIThread.RunJobs();
        var center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)
                     ?? new Point(200, 150);

        var pointer = window.TouchBegin(center);
        // 必须让主循环空转:Holding 是 DispatcherTimer 到点才发的,RunJobs 泵不到计时器
        await Task.Delay(700);
        window.TouchEnd(pointer, center);
        await Task.Delay(150);

        flyout?.Hide();
        window.Close();
        await Task.Delay(50);
        Console.WriteLine($"[longpress-click] {(guarded ? "guard" : "plain")}: ContextRequested={contexts} Click={clicks}");
        return (contexts, clicks);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("[longpress-click] FAIL: " + message);
        Console.WriteLine("[longpress-click] " + message);
    }
}
