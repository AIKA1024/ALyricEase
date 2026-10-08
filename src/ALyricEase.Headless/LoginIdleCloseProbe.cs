using System.Diagnostics;
using ALyricEase.Infrastructure;
using ALyricEase.Views;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Automation;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace ALyricEase.Headless;

/// <summary>无登录活动的关闭耗时诊断，并通过受控 UI 阻塞验证动画停帧机制。</summary>
internal static class LoginIdleCloseProbe
{
    public static async Task RunAsync()
    {
        var main = QqUserFailureProbe.CreateOfflineMain(
            Path.Combine(Path.GetTempPath(), $"aly-login-idle-{Guid.NewGuid():N}"));
        var view = new LoginDialogView { DataContext = main, IsVisible = false };
        view.Bind(ModalVisibilityTransition.IsOpenProperty, new Binding(nameof(main.IsLoginDialogOpen)));
        var window = new Window
        {
            Width = 1100, Height = 760,
            Content = new Grid { Children = { new AppShell { DataContext = main }, view } },
        };
        window.Show();
        try
        {
            for (var i = 0; i < 12; i++)
            {
                main.OpenLoginDialogCommand.Execute(null);
                await ModalVisibilityTransition.WaitForTransitionAsync(view);
                await Task.Delay(i % 3 == 0 ? 1100 : 50);
                var qqTab = view.GetVisualDescendants().OfType<Button>()
                    .Single(b => AutomationProperties.GetName(b) == "选择QQ音乐登录" ||
                                 AutomationProperties.GetName(b) == "选择 QQ 音乐登录");
                qqTab.Focus();
                // 头一次延长淡出，给 headless 输入泵留出时间，避免测试在动画已经结束后才发键。
                if (i == 0) ModalVisibilityTransition.SetDuration(view, TimeSpan.FromSeconds(2));
                var watch = Stopwatch.StartNew();
                main.CloseLoginDialogCommand.Execute(null);
                var commandMs = watch.Elapsed.TotalMilliseconds;
                if (!view.IsEnabled || !qqTab.IsEffectivelyEnabled || view.IsHitTestVisible)
                    throw new InvalidOperationException("淡出期间必须保持按钮外观并阻止指针命中。");
                if (i == 0)
                {
                    foreach (var key in new[] { Key.Enter, Key.Space, Key.Tab })
                    {
                        window.KeyPress(key, RawInputModifiers.None, default, null);
                        window.KeyRelease(key, RawInputModifiers.None, default, null);
                    }
                    if (main.Playlist.IsQQLoginTab || window.FocusManager!.GetFocusedElement() != qqTab)
                        throw new InvalidOperationException("淡出期间键盘仍能操作按钮或切换焦点。");
                }
                var transition = ModalVisibilityTransition.WaitForTransitionAsync(view);
                var intermediate = false;
                while (!transition.IsCompleted)
                {
                    await Task.Delay(10);
                    intermediate |= view.Opacity is > 0 and < 1;
                }
                await transition;
                if (view.IsVisible || view.IsEnabled)
                    throw new InvalidOperationException("动画完成后必须隐藏并禁用弹窗。");
                ModalVisibilityTransition.SetDuration(view, TimeSpan.FromMilliseconds(150));
                Console.WriteLine($"[login-idle] {i}: command={commandMs:F1}ms, total={watch.Elapsed.TotalMilliseconds:F1}ms, intermediate={intermediate}");
            }

            main.OpenLoginDialogCommand.Execute(null);
            await ModalVisibilityTransition.WaitForTransitionAsync(view);
            var cookieBox = view.FindControl<TextBox>("MusicUBox")!;
            cookieBox.Focus();
            var beforeText = cookieBox.Text;
            ModalVisibilityTransition.SetDuration(view, TimeSpan.FromSeconds(1));
            main.CloseLoginDialogCommand.Execute(null);
            window.KeyTextInput("must-not-be-entered");
            if (cookieBox.Text != beforeText)
                throw new InvalidOperationException("淡出期间文本输入未被拦截。");
            await ModalVisibilityTransition.WaitForTransitionAsync(view);
            ModalVisibilityTransition.SetDuration(view, TimeSpan.FromMilliseconds(150));

            main.OpenLoginDialogCommand.Execute(null);
            await ModalVisibilityTransition.WaitForTransitionAsync(view);
            main.CloseLoginDialogCommand.Execute(null);
            await Task.Delay(40);
            var opacity = view.Opacity;
            var stall = Stopwatch.StartNew();
            Thread.Sleep(700); // 故意模拟页面建树/数据绑定等 UI 线程工作；不是实际登录清理。
            Console.WriteLine($"[login-idle] injected UI stall={stall.Elapsed.TotalMilliseconds:F0}ms, opacity={opacity:F3}->{view.Opacity:F3}, visible={view.IsVisible}");
            if (opacity is <= 0 or >= 1 || Math.Abs(view.Opacity - opacity) > 0.0001 || !view.IsVisible)
                throw new InvalidOperationException("未复现 UI 动画在阻塞期间停止推进，需重新检查动画实现。");
            if (!view.IsEnabled)
                throw new InvalidOperationException("UI 停帧时不应出现禁用外观。");
            await ModalVisibilityTransition.WaitForTransitionAsync(view);

            main.OpenLoginDialogCommand.Execute(null);
            await ModalVisibilityTransition.WaitForTransitionAsync(view);
            main.CloseLoginDialogCommand.Execute(null);
            var interrupted = ModalVisibilityTransition.WaitForTransitionAsync(view);
            await Task.Delay(40);
            main.OpenLoginDialogCommand.Execute(null);
            await ModalVisibilityTransition.WaitForTransitionAsync(view);
            await interrupted;
            if (!view.IsVisible || !view.IsEnabled || !view.IsHitTestVisible)
                throw new InvalidOperationException("快速重开后被旧关闭回调隐藏或禁用。");
            main.CloseLoginDialogCommand.Execute(null);
            await ModalVisibilityTransition.WaitForTransitionAsync(view);
            Console.WriteLine("[login-idle] PASS 正常外观、键盘拦截、隐藏后禁用及快速重开");
        }
        finally { window.Close(); }
    }
}
