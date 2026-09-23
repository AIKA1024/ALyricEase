using System;
using System.Numerics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Animations;

namespace ALyricEase;

/// <summary>启动画面:极轻量窗口,进程启动即显示;主窗口的重量级构建(XAML 实例化等)
/// 延后到它渲染出首帧之后再做,做完才关闭衔接,消除"启动卡白屏"。
/// 旋转指示走合成线程 —— UI 线程被主窗口构建阻塞时它照样转。</summary>
public partial class SplashWindow : Window
{
  // ⚠ 必须在构造时就创建:Opened 在 Show() 里【同步】触发,而 WaitForReadyAsync 在那之后才被调用 ——
  //   若等到调用时才 new,Opened 里 TrySetResult 打在 null 上,信号丢失 → await 永久挂起,
  //   主窗口永不构建(2026-09-23 实测"启动后什么窗口都没有卡住")。
  private readonly TaskCompletionSource _openedTcs =
    new(TaskCreationOptions.RunContinuationsAsynchronously);

  public SplashWindow()
  {
    InitializeComponent();
    Opened += OnOpened;
  }

  /// <summary>等待启动画面完成布局并至少提交一帧,之后才安全地做重量级构建。
  /// 带 5 秒超时兜底:Opened 万一不来,宁可没有启动动效也不能挂死启动。</summary>
  public async Task WaitForReadyAsync()
  {
    await _openedTcs.Task;

    // 再等两帧:确保启动画面确实被合成呈现过,而不是只在 UI 线程排了队
    if (TopLevel.GetTopLevel(this) is not { } topLevel) return;
    var remaining = 2;
    var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    void Tick(TimeSpan _)
    {
      if (--remaining > 0) topLevel.RequestAnimationFrame(Tick);
      else tcs.TrySetResult();
    }
    topLevel.RequestAnimationFrame(Tick);
    await Task.WhenAny(tcs.Task, Task.Delay(5000));
  }

  private void OnOpened(object? sender, EventArgs e)
  {
    try
    {
      // Avalonia 组合视觉没有 WinUI 的角度制 "Rotation";RotationAxis 也不暴露(默认即 Z 轴),
      // 只能动画 RotationAngle(弧度)
      if (ElementComposition.GetElementVisual(Spinner) is { } visual)
      {
        visual.CenterPoint = new Vector3(11f, 11f, 0);
        var rotation = visual.Compositor.CreateScalarKeyFrameAnimation();
        rotation.Duration = TimeSpan.FromMilliseconds(900);
        rotation.IterationBehavior = AnimationIterationBehavior.Forever;
        rotation.InsertKeyFrame(0f, 0f);
        rotation.InsertKeyFrame(1f, MathF.PI * 2f);
        visual.StartAnimation("RotationAngle", rotation);
      }
    }
    catch
    {
      // 旋转指示失败只损失动效,绝不能把启动崩掉
    }
    finally
    {
      _openedTcs.TrySetResult();
    }
  }
}
