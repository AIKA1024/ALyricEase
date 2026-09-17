namespace ALyricEase.Services.Audio;

/// <summary>音频播放器抽象,便于替换后端。所有事件回调保证已在 UI 线程。</summary>
public interface IAudioPlayer : IDisposable
{
    PlaybackState State { get; }

    /// <summary>读当前进度;写 = Seek(毫秒)。</summary>
    long PositionMs { get; set; }

    /// <summary>总时长,毫秒;-1 表示未知。</summary>
    long DurationMs { get; }

    /// <summary>音量 0..100。</summary>
    int Volume { get; set; }

    event EventHandler? StateChanged;

    event EventHandler<long>? PositionChanged;

    event EventHandler<long>? DurationChanged;

    event EventHandler<string>? ErrorOccurred;

    void PlayUrl(string url);

    void Pause();

    void Resume();

    void Stop();

    // ---- 音频输出设备(可选能力) ----
    // 默认实现 = 不支持:后端不覆写这几项时,设置页显示"当前平台不支持切换输出设备",
    // 于是 Android/无头桩不需要为此写空实现。
    // 线程契约:这两个方法都由 UI 线程调用,实现内部负责把 WinRT/Android 的异步或
    // 平台调用挪到安全线程,返回时调用方仍在 UI 线程 —— 可直接更新绑定与状态栏。    /// <summary>后端是否支持枚举/切换音频输出设备。</summary>
    bool SupportsOutputDeviceSelection => false;

    /// <summary>最近一次枚举到的输出设备(不含"跟随系统默认设备"这一伪项);未枚举时为空。</summary>
    IReadOnlyList<AudioOutputDevice> OutputDevices => Array.Empty<AudioOutputDevice>();

    /// <summary>当前生效的设备 Id;null = 跟随系统默认设备。</summary>
    string? OutputDeviceId => null;

    /// <summary>重新枚举输出设备并返回最新列表(不走 <see cref="OutputDevices"/> 缓存)。
    /// 不支持的后端返回空列表。注意枚举失败也会返回空列表 —— 判定"设备没了"要看是否抛错,不能只看条数。</summary>
    Task<IReadOnlyList<AudioOutputDevice>> RefreshOutputDevicesAsync() =>
        Task.FromResult<IReadOnlyList<AudioOutputDevice>>(Array.Empty<AudioOutputDevice>());

    /// <summary>切换输出设备(null = 跟随系统默认设备)。返回是否已应用;
    /// 返回 false 时**原设备保持不变**(调用方据此回滚 UI 选择)。</summary>
    bool TrySetOutputDevice(string? deviceId) => false;

    // ---- 交叉淡化(可选能力) ----

    /// <summary>后端是否支持交叉淡化(双实例渐变切换)。</summary>
    bool SupportsCrossfade => false;

    /// <summary>交叉切换到新音源:旧曲继续播并渐弱、新曲从 0 渐强,到点停掉旧曲。
    /// 仅当当前正在播放且启用了交叉淡化时执行并返回 true;否则返回 false,
    /// 调用方回落 <see cref="PlayUrl"/> 硬切。由 UI 线程调用。</summary>
    Task<bool> TryCrossfadePlayAsync(string url, int fadeMs) => Task.FromResult(false);
}
