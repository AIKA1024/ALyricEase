using System;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace ALyricEase.Infrastructure;

/// <summary>UI 线程调度封装,消除业务代码对 Dispatcher 的直接依赖。
/// 播放器事件回调发生在后台线程,统一经此处转到 UI 线程。</summary>
public sealed class DispatcherService
{
    public void Post(Action action) => Dispatcher.UIThread.Post(action);

    /// <summary>在 UI 线程执行并等待完成(可 await),供后台线程安全更新 ObservableCollection。</summary>
    public Task InvokeAsync(Action action) => Dispatcher.UIThread.InvokeAsync(action).GetTask();
}
