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
  private TaskCompletionSource? _openedTcs;

  public SplashWindow()
  {
    InitializeComponent();
    Opened += OnOpened;
  }

  /// <summary>等待启动画面完成布局并至少提交一帧,之后才安全地做重量级构建。</summary>
  public async Task WaitForReadyAsync()
  {
    var opened = _openedTcs ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    await opened.Task;

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
    await tcs.Task;
  }

  private void OnOpened(object? sender, EventArgs e)
  {
    // 旋转指示走合成线程:回调里设好 CenterPoint 后启动 0→360° 循环
    if (ElementComposition.GetElementVisual(Spinner) is { } visual)
    {
      visual.CenterPoint = new Vector3(11f, 11f, 0);
      var rotation = visual.Compositor.CreateScalarKeyFrameAnimation();
      rotation.Duration = TimeSpan.FromMilliseconds(900);
      rotation.IterationBehavior = AnimationIterationBehavior.Forever;
      rotation.InsertKeyFrame(0f, 0f);
      rotation.InsertKeyFrame(1f, 360f);
      visual.StartAnimation("Rotation", rotation);
    }

    _openedTcs?.TrySetResult();
  }
}
