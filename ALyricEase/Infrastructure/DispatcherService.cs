using Avalonia.Threading;

namespace ALyricEase.Infrastructure;

/// <summary>UI 线程调度封装,消除业务代码对 Dispatcher 的直接依赖。
/// LibVLC 事件回调发生在播放器线程,统一经此处转到 UI 线程。</summary>
public sealed class DispatcherService
{
    public void Post(Action action) => Dispatcher.UIThread.Post(action);
}
