using System;
using System.Threading.Tasks;
using ALyricEase.Services.Audio;

namespace ALyricEase.Headless;

/// <summary>交叉淡化回归探针(--crossfade):用两个 StubAudioPlayer 驱动 CrossfadeAudioPlayer,
/// 验证包装层的切换语义,不碰真实音频。覆盖:
/// ① 未在播时请求交叉 → 拒绝(回落硬切);② 播放中交叉 → active 换到新实例、音量斜坡方向正确;
/// ③ 渐变结束后旧实例被 Stop 且音量复位;④ 渐变中用户改音量,斜坡按新值走;
/// ⑤ factory 为空 → SupportsCrossfade=false 且请求被拒;⑥ 输出设备调用转发到当前 active 实例。</summary>
internal static class CrossfadeProbe
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        void Check(bool ok, string name)
        {
            Console.WriteLine($"[crossfade] {(ok ? "PASS" : "FAIL")} {name}");
            if (!ok) failures++;
        }

        // ---- ⑤ factory 为空:不支持交叉 ----
        var noFade = new CrossfadeAudioPlayer(new HeadlessApp.StubAudioPlayer(), null);
        Check(!noFade.SupportsCrossfade, "无工厂时 SupportsCrossfade=false");
        Check(!await noFade.TryCrossfadePlayAsync("u", 300), "无工厂时拒绝交叉请求");

        // ---- 主场景 ----
        var primary = new HeadlessApp.StubAudioPlayer();
        var secondary = new HeadlessApp.StubAudioPlayer();
        var wrapper = new CrossfadeAudioPlayer(primary, () => secondary);

        // ⑦ 构造即转发:任何交叉发生前,primary 的事件就必须经包装层冒出。
        // 漏接线时 VM 收不到进度/时长/播完状态 —— 首曲自然播完的交叉与自动切歌全哑。
        var primaryStateEvents = 0;
        var primaryPositionEvents = 0;
        wrapper.StateChanged += (_, _) => primaryStateEvents++;
        wrapper.PositionChanged += (_, _) => primaryPositionEvents++;

        // ① 未在播:拒绝
        Check(!await wrapper.TryCrossfadePlayAsync("song-b", 300), "未在播时拒绝交叉请求");

        // 起播:硬切走 primary;模拟正在播放、用户音量 70
        wrapper.PlayUrl("song-a");
        primary.SetState(PlaybackState.Playing);
        primary.RaisePosition(1000);
        Check(primaryStateEvents > 0, "交叉发生前 primary 状态事件经包装层转发");
        Check(primaryPositionEvents > 0, "交叉发生前 primary 进度事件经包装层转发");
        wrapper.Volume = 70;
        Check(wrapper.State == PlaybackState.Playing && wrapper.Volume == 70,
            "硬切后状态/音量来自 primary");

        // 交叉淡化的开关由 VM 侧传参表达:这里直接走 TryCrossfadePlayAsync
        var swapped = await wrapper.TryCrossfadePlayAsync("song-b", fadeMs: 300);
        Check(swapped, "播放中交叉请求被接受");
        Check(wrapper.OutputDeviceId is null && secondary.Volume == 0,
            "切换后新实例音量从 0 起步");

        // ② 音量斜坡:中点采样,旧实例在减、新实例在增
        await Task.Delay(120);
        var midOld = primary.Volume;
        var midNew = secondary.Volume;
        Check(midOld is > 0 and < 70 && midNew is > 0 and < 70,
            $"渐变中点两实例音量都在半路(old={midOld}, new={midNew})");

        // ④ 渐变中改用户音量:斜坡按新值走
        wrapper.Volume = 40;

        // ③ 等渐变结束:旧实例被停、音量复位;新实例停在新的用户音量
        await Task.Delay(600);
        Check(primary.StopCallCount == 1, "渐变结束后旧实例被 Stop");
        Check(primary.Volume == 40, $"旧实例音量复位到用户音量(={primary.Volume})");
        Check(secondary.Volume == 40, $"新实例停在用户音量(={secondary.Volume})");
        Check(secondary.StopCallCount == 0, "新实例未被误停");

        // 回归(2026-09-21):渐变自然结束后 _fadeCts 曾不清空 ⇒ Volume setter 一直走
        // "渐变进行中"分支,拖音量条只改 _userVolume 永不落到播放实例。
        // 修复后(会话标记被清)音量设置必须立即生效。
        wrapper.Volume = 55;
        Check(secondary.Volume == 55, $"渐变结束后音量设置立即生效(={secondary.Volume})");

        // 事件转发:active 换到 secondary 后,其状态事件应经 wrapper 冒出
        var sawStateEvent = false;
        wrapper.StateChanged += (_, _) => sawStateEvent = true;
        secondary.SetState(PlaybackState.Playing);
        Check(sawStateEvent, "active 实例的状态事件经包装层转发");

        // ⑥ 输出设备转发:落点应是当前 active(secondary)
        var applied = wrapper.TrySetOutputDevice("dev-1");
        Check(applied && secondary.OutputDeviceId == "dev-1",
            "输出设备设置转发到当前 active 实例");
        Check(wrapper.OutputDeviceId == "dev-1", "OutputDeviceId 回读自 active");

        // Stop:两个实例都停、渐变取消(此时无渐变,验证不抛即可)
        wrapper.Stop();
        Check(primary.StopCallCount == 2 && secondary.StopCallCount == 1,
            "Stop 作用于两个实例");

        Console.WriteLine(failures == 0
            ? "[crossfade] 全部通过"
            : $"[crossfade] {failures} 项失败");
        return failures == 0 ? 0 : 1;
    }
}
