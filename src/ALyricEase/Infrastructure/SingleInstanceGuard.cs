using Microsoft.Extensions.Logging;

namespace ALyricEase.Infrastructure;

/// <summary>桌面端单实例守卫:首个进程持有命名互斥量并监听激活事件,
/// 后续进程只负责把首实例的窗口叫回前台,然后自己退出。
///
/// 用"命名互斥量 + 命名事件"而不是"按进程名计数":本应用有安装版(current)、
/// 本地 AOT 输出(artifacts)两种 exe 路径,更新前后还可能新旧并存,进程名与路径都不可靠;
/// 命名内核对象是会话级唯一标识,与 exe 路径无关。
///
/// 放到核心项目是为了让自更新流程(VelopackUpdateService)也能在重启前主动让出锁,
/// 否则 Update.exe 拉起的新进程会把自己当成"第二个实例"直接退出,更新完应用就起不来。</summary>
public static class SingleInstanceGuard
{
    // Local\ = 当前登录会话,不用 Global\:多用户同时登录时各自开一个是合理的。
    private const string MutexName = @"Local\ALyricEase.SingleInstance";
    private const string ActivationEventName = @"Local\ALyricEase.SingleInstance.Activate";

    /// <summary>首实例可能还在启动(事件对象尚未创建),等它一小会儿而不是立刻判定"没在跑"。</summary>
    private const int ActivationRetryCount = 20;
    private const int ActivationRetryDelayMs = 50;

    private static Mutex? _mutex;
    private static EventWaitHandle? _activationEvent;
    private static RegisteredWaitHandle? _waitRegistration;
    private static Action? _activator;
    private static ILogger? _logger;
    private static int _activationPending;

    /// <summary>抢占单实例所有权。返回 false 表示已有实例在跑,激活信号已发出,调用方应立即退出。
    /// 守卫自身失败(权限/内核对象异常)时返回 true:宁可多开一个,也不能让应用打不开。</summary>
    public static bool TryAcquire(ILogger? logger = null)
    {
        if (!OperatingSystem.IsWindows()) return true; // Android 由系统保证单实例
        if (_mutex is not null) return true;

        _logger = logger;
        try
        {
            var mutex = new Mutex(initiallyOwned: false, MutexName);
            bool owned;
            try
            {
                owned = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                // 上一个实例被强杀/崩溃:所有权转给我们,照常当代唯一实例。
                owned = true;
            }

            if (!owned)
            {
                mutex.Dispose();
                RequestActivation(logger);
                return false;
            }

            _mutex = mutex;
            _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
            // 线程池等待,不额外养一条阻塞线程(本项目对常驻开销敏感)。
            _waitRegistration = ThreadPool.RegisterWaitForSingleObject(
                _activationEvent, OnActivationSignaled, null, Timeout.Infinite, executeOnlyOnce: false);
            logger?.LogInformation("Single instance acquired. Mutex={MutexName}", MutexName);
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Single instance guard unavailable; continuing without it");
            Release();
            return true;
        }
    }

    /// <summary>注册"把窗口叫出来"的动作。必须由 UI 线程在窗口建好后调用;
    /// 若启动期间已经收到过激活请求,这里会立刻补发一次。</summary>
    public static void AttachActivator(Action activate, ILogger? logger = null)
    {
        _logger ??= logger;
        Volatile.Write(ref _activator, activate);
        if (Interlocked.Exchange(ref _activationPending, 0) == 1)
            activate();
    }

    /// <summary>释放所有权。退出前调用;自更新重启前也必须调用。</summary>
    public static void Release()
    {
        _waitRegistration?.Unregister(null);
        _waitRegistration = null;

        _activationEvent?.Dispose();
        _activationEvent = null;

        if (_mutex is not { } mutex) return;
        try
        {
            mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // 非持有线程调用(或被重复释放):交给进程退出时回收,不能因此崩在 finally 里。
        }
        mutex.Dispose();
        _mutex = null;
    }

    /// <summary>第二个进程:通知首实例激活。首实例可能仍在启动,重试到事件出现为止。</summary>
    private static void RequestActivation(ILogger? logger)
    {
        // 命名事件只有 Windows 支持;其它平台连 TryAcquire 都不会走到这里。
        if (!OperatingSystem.IsWindows()) return;

        for (var attempt = 0; attempt < ActivationRetryCount; attempt++)
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(ActivationEventName, out var handle))
                {
                    using (handle) handle.Set();
                    logger?.LogInformation("Another instance is running; activation signal sent. Attempt={Attempt}",
                        attempt + 1);
                    return;
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to signal the running instance");
                return;
            }

            Thread.Sleep(ActivationRetryDelayMs);
        }

        // 锁确实被占着,但对方没暴露事件(极旧的版本/守卫异常):静默退出也比多开安全。
        logger?.LogWarning("Another instance holds the mutex but never exposed the activation event");
    }

    private static void OnActivationSignaled(object? state, bool timedOut)
    {
        if (timedOut) return;
        if (Volatile.Read(ref _activator) is not { } activator)
        {
            // 主窗口还没建好:记下来,AttachActivator 时补发。
            Interlocked.Exchange(ref _activationPending, 1);
            return;
        }

        try
        {
            activator();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Single instance activation callback failed");
        }
    }
}
