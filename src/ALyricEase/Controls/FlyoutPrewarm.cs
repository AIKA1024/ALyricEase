using System;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Logging;

namespace ALyricEase.Controls;

/// <summary>
/// 菜单/Flyout 的"第一次打开"预热。
///
/// 首次 <see cref="FlyoutBase.ShowAt(Control)"/> 要付一笔 230~320ms 的一次性成本(菜单模板实例化、
/// ControlTheme/Style 首次匹配、建弹窗窗口、首次合成),而这笔钱是花在[打开动画开始之前]的:
/// 探针(--menu-frames)实测首轮 ShowAt→弹窗首次可见 249~319ms,等第一帧可见时 250ms 的展开动画
/// 已经跑掉 143ms(≈84%),屏幕上看到的是"菜单弹出来";第二、三次打开 ShowAt→可见只要 38ms,
/// 动画完整可见。同一份代码两种观感,差别全在这里。
///
/// **AOT 缓解不了多少**:Release 构建(优化后 JIT)首轮仍量到 319ms,与 Debug 同量级 ⇒ 瓶颈不在
/// JIT/托管代码;其中光"建弹窗 HWND + 首次合成"就要 71ms(裸 Popup 实测),那是 OS/DWM 的钱,
/// NativeAOT 一分都省不掉,而 csproj 里 OptimizationPreference=Size 还会削弱余下那部分。
///
/// 所以把首笔成本挪到用户看不见的时刻:预热只走一次 ShowAt + Hide,中间不发生渲染,
/// 屏幕上不会闪东西。<see cref="WarmMenu"/> 放启动画面后面(那时 UI 线程本来就空闲),
/// <see cref="WarmFlyout"/> 放壳层首帧之后的 idle,避免把启动关键路径拖长。
/// </summary>
internal static class FlyoutPrewarm
{
    private static bool _menuWarmed;
    private static bool _flyoutWarmed;

    /// <summary>菜单路径是否已预热(探针用)。</summary>
    internal static bool IsMenuWarmed => _menuWarmed;

    /// <summary>普通 Flyout 路径是否已预热(探针用)。</summary>
    internal static bool IsFlyoutWarmed => _flyoutWarmed;

    /// <summary>预热菜单(MenuFlyoutPresenter + MenuItem 容器 + 弹窗窗口创建)。
    /// <paramref name="anchor"/> 必须已在可视树中:弹窗要按它的位置计算,并继承它的 TopLevel 与 Theme。</summary>
    internal static void WarmMenu(Control anchor)
    {
        if (_menuWarmed)
        {
            return;
        }

        _menuWarmed = true;
        Warm(anchor, "MenuFlyout", new MenuFlyout(), m => m.Items.Add(new MenuItem { Header = "预" }));
    }

    /// <summary>预热普通 Flyout(FlyoutPresenter —— 音量、歌词字号等走这条路径,模板与菜单不同)。</summary>
    internal static void WarmFlyout(Control anchor)
    {
        if (_flyoutWarmed)
        {
            return;
        }

        _flyoutWarmed = true;
        Warm(anchor, "Flyout", new Flyout(), f => f.Content = new TextBlock { Text = "预" });
    }

    private static void Warm<T>(Control anchor, string name, T flyout, Action<T> fill) where T : FlyoutBase
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            fill(flyout);

            // ShowAt 内部同步建模板 + 建弹窗窗口;紧跟着 Hide 关掉,
            // 期间不会发生渲染回合,所以不会在屏幕上闪一下。
            flyout.ShowAt(anchor);
            flyout.Hide();
        }
        catch (Exception ex)
        {
            // 预热失败不影响功能,只是首次打开慢一点 —— 不能让它把启动流程带崩。
            Logger.TryGet(LogEventLevel.Warning, LogArea.Control)?.Log(anchor,
                $"FlyoutPrewarm: 预热 {name} 失败: {ex.Message}");
            return;
        }

        Logger.TryGet(LogEventLevel.Debug, LogArea.Control)?.Log(anchor,
            $"FlyoutPrewarm: {name} {Stopwatch.GetElapsedTime(started).TotalMilliseconds:0}ms");
    }
}
