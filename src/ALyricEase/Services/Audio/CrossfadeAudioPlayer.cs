using System;
using System.Threading;
using System.Threading.Tasks;

namespace ALyricEase.Services.Audio;

/// <summary>交叉淡化播放器:包住平台后端,内部持有两个实例交替播放。
/// 切歌时旧实例继续播并渐弱、新实例从 0 渐强(音量斜坡),到点后停掉旧实例留作下次复用;
/// 平台后端只负责"播"和"音量",渐变节奏统一在这里,Windows/Android 共用一份实现。
///
/// 线程契约:PlayUrl/TryCrossfadePlayAsync/Pause 等控制方法由 UI 线程调用(与 IAudioPlayer 一致);
/// 音量斜坡跑在线程池,只碰各后端的 Volume/Stop(均为线程安全的属性赋值/内部自锁调用)。
/// 事件/状态/进度一律转发当前 active 实例;旧实例在切换瞬间就解除事件订阅,
/// 它渐变结束转 Idle 不会漏给上层(否则会被 PlayerViewModel 当成"播完自动切歌")。</summary>
public sealed class CrossfadeAudioPlayer : IAudioPlayer
{
    private readonly Func<IAudioPlayer>? _secondaryFactory;
    private readonly IAudioPlayer?[] _players = new IAudioPlayer?[2];
    private int _activeIndex;

    private int _userVolume;
    private string? _deviceId;

    // 渐变会话:同一时刻最多一个;硬切/Stop 时取消,旧实例音量停在半路由 Stop 兜底。
    // volatile:自然结束由线程池清空,Volume setter(UI 线程)读它决定"立即应用还是交给斜坡"。
    private volatile CancellationTokenSource? _fadeCts;

    // active 实例的事件转发(handler 存字段以便交换后退订旧实例)
    private EventHandler? _onStateChanged;
    private EventHandler<long>? _onPositionChanged;
    private EventHandler<long>? _onDurationChanged;
    private EventHandler<string>? _onErrorOccurred;

    public CrossfadeAudioPlayer(IAudioPlayer primary, Func<IAudioPlayer>? secondaryFactory)
    {
        _players[0] = primary ?? throw new ArgumentNullException(nameof(primary));
        _secondaryFactory = secondaryFactory;
        _userVolume = Math.Clamp(primary.Volume, 0, 100);
        Wire(primary); // 构造即接线:primary 的事件从第一首歌起就要转发。
                       // 漏掉这条时 VM 在首次交叉前收不到任何进度/时长/状态事件 ——
                       // 进度条不走、自然播完既无交叉窗口也无自动切歌(真机回归 2026-09-17)。
    }

    private IAudioPlayer Active => _players[_activeIndex]!;

    private static void SetVolumeSafe(IAudioPlayer player, int volume)
    {
        try
        {
            player.Volume = volume;
        }
        catch
        {
            // 实例可能刚被 Stop/异常:音量写不进去不影响下一次起播(起播前会再压 0)
        }
    }

    // ---- 交叉淡化(IAudioPlayer 可选能力) ----

    public bool SupportsCrossfade => _secondaryFactory is not null;

    public async Task<bool> TryCrossfadePlayAsync(string url, int fadeMs)
    {
        // 开关由调用方(VM)按设置判断后才发起:这里只看"有没有在响的旧曲"
        if (fadeMs <= 0 || _secondaryFactory is null) return false;
        var old = Active;
        if (old.State != PlaybackState.Playing) return false; // 没有在响的旧曲,谈不上交叉

        if (_players[1] is null)
        {
            var created = _secondaryFactory();
            _players[1] = created;
            if (_deviceId is { Length: > 0 })
                await ApplyDeviceAsync(created); // Windows 的 AudioDevice 只认自家枚举的实例,副实例要自己枚举一遍
        }

        CancelFade();
        var fresh = _players[1 - _activeIndex]!;
        Unwire(old);
        _activeIndex = 1 - _activeIndex; // 交换 active:此后状态/进度来自新曲
        Wire(fresh);

        fresh.Volume = 0;
        try
        {
            fresh.PlayUrl(url);
        }
        catch
        {
            // 新实例起播失败:退回旧实例继续播,报告未执行让调用方走硬切
            Unwire(fresh);
            _activeIndex = 1 - _activeIndex;
            Wire(old);
            return false;
        }

        StartFade(old, fresh, fadeMs);
        return true;
    }

    /// <summary>音量斜坡:旧实例 u→0、新实例 0→u(每步读最新的用户音量,拖滑条立即跟进)。
    /// 到点后停掉旧实例并复位其音量(实例保留,下次交叉复用,不 Dispose)。</summary>
    private void StartFade(IAudioPlayer oldPlayer, IAudioPlayer newPlayer, int fadeMs)
    {
        var cts = _fadeCts = new CancellationTokenSource();
        var token = cts.Token;
        _ = Task.Run(async () =>
        {
            var steps = Math.Clamp(fadeMs / 50, 4, 80);
            var delay = Math.Max(10, fadeMs / steps);
            try
            {
                try
                {
                    for (var i = 1; i <= steps; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        var p = i / (double)steps;
                        var u = Volatile.Read(ref _userVolume) / 100.0;
                        SetVolumeSafe(oldPlayer, (int)Math.Round(u * (1 - p) * 100));
                        SetVolumeSafe(newPlayer, (int)Math.Round(u * p * 100));
                        await Task.Delay(delay, token);
                    }
                }
                catch (OperationCanceledException)
                {
                    return; // 硬切/Stop 接管了两个实例,这里什么都别再碰
                }

                SetVolumeSafe(oldPlayer, 0);
                try
                {
                    oldPlayer.Stop();
                }
                catch
                {
                    // 旧实例已出问题也没关系:它不再被监听
                }

                SetVolumeSafe(oldPlayer, Volatile.Read(ref _userVolume)); // 复位留复用;起播前还会再压 0
            }
            finally
            {
                // 渐变自然结束后清掉会话标记:漏掉这条时 Volume setter 会一直认为
                // "渐变进行中",拖音量条只改 _userVolume、永不落到播放实例(回归 2026-09-21)。
                // CAS 只清自己的会话:期间若已换上新 CTS(硬切/新交叉),不动它。
                Interlocked.CompareExchange(ref _fadeCts, null, cts);
            }
        });
    }

    private void CancelFade()
    {
        var cts = _fadeCts;
        _fadeCts = null;
        if (cts is null) return;
        cts.Cancel();
        cts.Dispose();
    }

    private async Task ApplyDeviceAsync(IAudioPlayer player)
    {
        try
        {
            await player.RefreshOutputDevicesAsync();
        }
        catch
        {
            // 枚举失败就用不了指定设备:回落系统默认,不阻塞切歌
        }

        try
        {
            player.TrySetOutputDevice(_deviceId);
        }
        catch
        {
            // 同上
        }
    }

    // ---- IAudioPlayer 基本控制 ----

    public PlaybackState State => Active.State;

    public long PositionMs
    {
        get => Active.PositionMs;
        set => Active.PositionMs = value;
    }

    public long DurationMs => Active.DurationMs;

    public int Volume
    {
        get => _userVolume;
        set
        {
            _userVolume = Math.Clamp(value, 0, 100);
            // 渐变进行中交给斜坡调度(每步都读最新值);静止时立即生效
            if (_fadeCts is null) SetVolumeSafe(Active, _userVolume);
        }
    }

    public void PlayUrl(string url)
    {
        CancelFade();
        // 另一个实例若还握着旧音源(上次交叉被硬切打断),停掉释放
        var other = _players[1 - _activeIndex];
        if (other is { State: not PlaybackState.Idle })
            SetVolumeSafe(other, _userVolume);
        if (other is not null)
        {
            try
            {
                other.Stop();
            }
            catch
            {
                // 停不掉就让它去:不再被监听
            }
        }

        Active.PlayUrl(url);
    }

    public void Pause()
    {
        // 渐变中两侧都在响:一起停;静止时另一侧本来就 Idle,多调无害
        foreach (var player in _players)
            if (player is { State: PlaybackState.Playing })
            {
                try
                {
                    player.Pause();
                }
                catch
                {
                    // 单侧失败不拖累另一侧
                }
            }
    }

    public void Resume()
    {
        foreach (var player in _players)
            if (player is { State: PlaybackState.Paused })
            {
                try
                {
                    player.Resume();
                }
                catch
                {
                    // 同上
                }
            }
    }

    public void Stop()
    {
        CancelFade();
        foreach (var player in _players)
            if (player is not null)
            {
                SetVolumeSafe(player, _userVolume); // 渐变半路的音量复位
                try
                {
                    player.Stop();
                }
                catch
                {
                    // 同上
                }
            }
    }

    public void Dispose()
    {
        CancelFade();
        Unwire(Active);
        foreach (var player in _players)
            player?.Dispose();
    }

    // ---- 事件转发(仅 active 实例) ----

    public event EventHandler? StateChanged;

    public event EventHandler<long>? PositionChanged;

    public event EventHandler<long>? DurationChanged;

    public event EventHandler<string>? ErrorOccurred;

    private void Wire(IAudioPlayer player)
    {
        _onStateChanged = (_, e) => StateChanged?.Invoke(this, e);
        _onPositionChanged = (_, e) => PositionChanged?.Invoke(this, e);
        _onDurationChanged = (_, e) => DurationChanged?.Invoke(this, e);
        _onErrorOccurred = (_, e) => ErrorOccurred?.Invoke(this, e);
        player.StateChanged += _onStateChanged;
        player.PositionChanged += _onPositionChanged;
        player.DurationChanged += _onDurationChanged;
        player.ErrorOccurred += _onErrorOccurred;
    }

    private void Unwire(IAudioPlayer player)
    {
        if (_onStateChanged is { } s) player.StateChanged -= s;
        if (_onPositionChanged is { } p) player.PositionChanged -= p;
        if (_onDurationChanged is { } d) player.DurationChanged -= d;
        if (_onErrorOccurred is { } e) player.ErrorOccurred -= e;
        _onStateChanged = null;
        _onPositionChanged = null;
        _onDurationChanged = null;
        _onErrorOccurred = null;
    }

    // ---- 音频输出设备(转发 active;副实例首次使用时补应用) ----

    public bool SupportsOutputDeviceSelection => Active.SupportsOutputDeviceSelection;

    public IReadOnlyList<AudioOutputDevice> OutputDevices => Active.OutputDevices;

    public string? OutputDeviceId => Active.OutputDeviceId;

    public Task<IReadOnlyList<AudioOutputDevice>> RefreshOutputDevicesAsync() =>
        Active.RefreshOutputDevicesAsync();

    public bool TrySetOutputDevice(string? deviceId)
    {
        _deviceId = deviceId; // 副实例创建时照此应用
        return Active.TrySetOutputDevice(deviceId);
    }
}
