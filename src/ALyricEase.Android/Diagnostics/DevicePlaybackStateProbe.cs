using System;
using System.Threading.Tasks;
using ALyricEase.Infrastructure;
using ALyricEase.Services.Audio;
using Avalonia.Threading;
using PlayerConstants = AndroidX.Media3.Common.BasePlayer.InterfaceConsts;

namespace ALyricEase.Diagnostics;

/// <summary>Android 真机自检:播放后端有没有把状态/时长/进度报到上层。
///
/// 存在的理由:Media3 的 <c>Player.Listener</c> 全是 Java 默认方法,在 .NET 绑定里是**抽象成员**,
/// 漏实现任何一个都会被 java-interop 的桩换成运行时 <c>AbstractMethodError</c>;
/// 而"只实现 onEvents + <c>events.Contains(...)</c> 过滤"这条路在 Release 绑定里恒为 false,
/// 表现为"音频真的在播,界面却停在 0:00 / 未播放"(2026-10-28 用户报障,本探针当场复现)。
/// 本探针不依赖登录与歌单:直接用生产的 <see cref="IAudioPlayer"/> 播一段公开样音,
/// 把状态常量、包装器转发到上层的事件、以及每秒快照全部打到 logcat。
/// <code>
/// adb shell am start -n com.aika1024.alyricease/crc64ea7453dff8e094fe.MainActivity --es aly_probe playback-state
/// adb logcat -s ALyricEaseProbe:I
/// </code>
/// 期望:`StateChanged → Loading` → `DurationChanged → 19173ms` → `StateChanged → Playing`,
/// 之后每 200ms 一条 `PositionChanged`,收尾 `Stop()` 后回到 `Idle`。</summary>
internal static class DevicePlaybackStateProbe
{
    internal const string IntentExtraValue = "playback-state";

    private const string Tag = "ALyricEaseProbe";
    private const string SampleUrl = "https://download.samplelib.com/mp3/sample-15s.mp3";
    private const int Seconds = 12;

    public static void Run()
    {
        try
        {
            Emit($"常量 idle={PlayerConstants.StateIdle} buffering={PlayerConstants.StateBuffering} " +
                 $"ready={PlayerConstants.StateReady} ended={PlayerConstants.StateEnded}");
            Emit($"事件位 state={PlayerConstants.EventPlaybackStateChanged} playing={PlayerConstants.EventIsPlayingChanged} " +
                 $"pwr={PlayerConstants.EventPlayWhenReadyChanged} tl={PlayerConstants.EventTimelineChanged} " +
                 $"disc={PlayerConstants.EventPositionDiscontinuity} sess={PlayerConstants.EventAudioSessionId}");
            Dispatcher.UIThread.Post(() => _ = RunAsync());
        }
        catch (Exception ex)
        {
            Emit("启动失败: " + ex);
        }
    }

    private static async Task RunAsync()
    {
        try
        {
            var player = ServiceLocator.Get<IAudioPlayer>();
            Emit($"player={player.GetType().Name} supportsCrossfade={player.SupportsCrossfade} state={player.State}");

            player.StateChanged += (_, _) => Emit($"[VM 收到] StateChanged → {player.State}");
            player.PositionChanged += (_, ms) => Emit($"[VM 收到] PositionChanged → {ms}ms");
            player.DurationChanged += (_, ms) => Emit($"[VM 收到] DurationChanged → {ms}ms");
            player.ErrorOccurred += (_, msg) => Emit($"[VM 收到] ErrorOccurred → {msg}");

            player.Volume = 100;
            Emit($"PlayUrl: {SampleUrl}");
            player.PlayUrl(SampleUrl);

            for (var i = 0; i < Seconds; i++)
            {
                await Task.Delay(1000);
                Emit($"t+{i + 1}s state={player.State} pos={player.PositionMs} dur={player.DurationMs}");
            }

            Emit("Stop()");
            player.Stop();
            await Task.Delay(500);
            Emit($"收尾 state={player.State} pos={player.PositionMs} dur={player.DurationMs}");
        }
        catch (Exception ex)
        {
            Emit("异常: " + ex);
        }
    }

    private static void Emit(string message) => global::Android.Util.Log.Info(Tag, message);
}
