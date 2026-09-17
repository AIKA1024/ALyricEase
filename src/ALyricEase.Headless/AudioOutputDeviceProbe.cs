using ALyricEase.Services;
using ALyricEase.Services.Audio;
using ALyricEase.ViewModels;

namespace ALyricEase.Headless;

/// <summary>音频输出设备设置回归(--audiodevice):全程用桩播放器 + 临时 state.json,不碰真实声卡与用户配置。
///
/// 要守住的不变量:
/// ① 下拉第 0 项恒为"跟随系统默认设备",索引 i 对应设备 i-1;
/// ② 选择立即落到播放器并落盘(下次启动按 Id 恢复);
/// ③ 设备 Id 变了(Windows 换 USB 口 / Android 重启)要能按展示名找回,并把新 Id 写回;
/// ④ 设备不在时回退系统默认,**但不覆写**用户保存的偏好(可能只是暂时拔掉);
/// ⑤ 应用失败必须回滚下拉显示,不能留下"选了却没生效"的假状态;
/// ⑥ 后端不支持时(老安卓/无头)隐藏下拉并给出说明。</summary>
internal static class AudioOutputDeviceProbe
{
    private const string SpeakerId = "dev-speaker";
    private const string SpeakerName = "扬声器 (Realtek(R) Audio)";
    private const string BtId = "dev-bt";
    private const string BtName = "蓝牙耳机";
    private const string DefaultOption = "跟随系统默认设备";

    public static int Run()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var root = Path.Combine(Path.GetTempPath(), $"aly-audiodevice-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var failures = 0;
        try
        {
            failures += FirstSelectionPersists(Path.Combine(root, "first"));
            failures += RestoreById(Path.Combine(root, "byid"));
            failures += RestoreByStaleIdFallsBackToName(Path.Combine(root, "byname"));
            failures += MissingDeviceFallsBackToDefault(Path.Combine(root, "missing"));
            failures += FailedSwitchRollsBack(Path.Combine(root, "fail"));
            failures += UnsupportedBackendHidesPicker(Path.Combine(root, "unsupported"));
            failures += RefreshKeepsSelection(Path.Combine(root, "refresh"));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }

        Console.WriteLine(failures == 0
            ? "[audiodevice] PASS 选项/索引映射、落盘与恢复、Id 失配按名回退、设备缺失回落默认、应用失败回滚、不支持后端隐藏"
            : $"[audiodevice] FAIL: {failures}");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>首次选择:选中即应用 + 落盘,重启后按 Id 恢复。</summary>
    private static int FirstSelectionPersists(string dir)
    {
        var failures = 0;
        var f = Create(dir, savedId: null, savedName: null, Speaker(), Bluetooth());

        Expect(ref failures, f.Vm.AudioDeviceSupported, "支持切换设备的后端应让 AudioDeviceSupported 为 true");
        Expect(ref failures, f.Vm.AudioDeviceOptions.Count == 3, $"下拉应有 3 项,实际 {f.Vm.AudioDeviceOptions.Count}");
        Expect(ref failures, f.Vm.AudioDeviceOptions[0] == DefaultOption, $"第 0 项应是「{DefaultOption}」");
        Expect(ref failures, f.Vm.AudioDeviceOptions[1] == $"{SpeakerName}（系统默认）",
            $"系统默认设备应带标注,实际「{f.Vm.AudioDeviceOptions[1]}」");
        Expect(ref failures, f.Vm.AudioDeviceIndex == 0, "未选过设备时索引应为 0(跟随系统默认)");
        Expect(ref failures, f.Player.OutputDeviceId is null, "未选过设备时播放器不应被指定设备");

        f.Vm.AudioDeviceIndex = 2; // 蓝牙耳机
        Expect(ref failures, f.Player.OutputDeviceId == BtId, "选中后应立即应用到播放器");
        Expect(ref failures, f.Vm.AudioDeviceIndex == 2, "选中后索引应停在第 2 项");
        Expect(ref failures, f.Vm.HasAudioDeviceHint == false, "成功切换不应留下提示");

        var reloaded = new AppStateStore(Path.Combine(dir, "state.json"));
        Expect(ref failures, reloaded.AudioOutputDeviceId == BtId, $"落盘的设备 Id 应是 {BtId},实际 {reloaded.AudioOutputDeviceId ?? "<null>"}");
        Expect(ref failures, reloaded.AudioOutputDeviceName == BtName, "落盘应带上展示名(供 Id 失配时回退匹配)");
        return failures;
    }

    /// <summary>重启:state.json 里的 Id 命中当前枚举列表 ⇒ 直接应用,并且不再写盘。</summary>
    private static int RestoreById(string dir)
    {
        var failures = 0;
        var f = Create(dir, BtId, BtName, Speaker(), Bluetooth());

        Expect(ref failures, f.Vm.AudioDeviceIndex == 2, $"应从落盘偏好恢复到第 2 项,实际 {f.Vm.AudioDeviceIndex}");
        Expect(ref failures, f.Player.OutputDeviceId == BtId, "恢复时应把设备贴到播放器");
        Expect(ref failures, f.Vm.HasAudioDeviceHint == false, "Id 命中时不应有提示");

        // 恢复路径不应因为"应用了一次"就重复写盘:Id 没变 ⇒ 文件里仍是原值
        var reloaded = new AppStateStore(Path.Combine(dir, "state.json"));
        Expect(ref failures, reloaded.AudioOutputDeviceId == BtId, "恢复过程不应改写已保存的 Id");
        return failures;
    }

    /// <summary>Id 失配但名字相同(Windows 换 USB 口 / Android 重启换 Id)⇒ 按名找回并写回新 Id。</summary>
    private static int RestoreByStaleIdFallsBackToName(string dir)
    {
        var failures = 0;
        var f = Create(dir, "stale-id-from-last-boot", BtName, Speaker(), Bluetooth());

        Expect(ref failures, f.Vm.AudioDeviceIndex == 2, $"名字相同时应找回设备(索引 2),实际 {f.Vm.AudioDeviceIndex}");
        Expect(ref failures, f.Player.OutputDeviceId == BtId, "找回后应应用到播放器");

        var reloaded = new AppStateStore(Path.Combine(dir, "state.json"));
        Expect(ref failures, reloaded.AudioOutputDeviceId == BtId, $"新 Id 应写回 state,实际 {reloaded.AudioOutputDeviceId ?? "<null>"}");
        return failures;
    }

    /// <summary>设备不在(拔掉了)⇒ 本次会话回退系统默认并提示,但不覆写用户保存的偏好。</summary>
    private static int MissingDeviceFallsBackToDefault(string dir)
    {
        var failures = 0;
        var f = Create(dir, "ghost-id", "已拔掉的耳机", Speaker());

        Expect(ref failures, f.Vm.AudioDeviceIndex == 0, $"设备缺失时应回落到第 0 项,实际 {f.Vm.AudioDeviceIndex}");
        Expect(ref failures, f.Player.OutputDeviceId is null, "设备缺失时不应给播放器指定任何设备");
        Expect(ref failures, f.Vm.HasAudioDeviceHint, "设备缺失应给出提示");
        Expect(ref failures, f.Vm.AudioDeviceHint?.Contains("已拔掉的耳机") == true,
            $"提示里应带上原设备名,实际「{f.Vm.AudioDeviceHint}」");

        var reloaded = new AppStateStore(Path.Combine(dir, "state.json"));
        Expect(ref failures, reloaded.AudioOutputDeviceId == "ghost-id",
            "设备暂时缺失不应抹掉用户保存的偏好(插回来还要认)");
        return failures;
    }

    /// <summary>应用失败 ⇒ 下拉回滚到实际生效项 + 提示,不写盘。</summary>
    private static int FailedSwitchRollsBack(string dir)
    {
        var failures = 0;
        var f = Create(dir, BtId, BtName, Speaker(), Bluetooth());
        Expect(ref failures, f.Vm.AudioDeviceIndex == 2, "用例前提:应从蓝牙耳机恢复");

        f.Player.FailDeviceId = SpeakerId; // 模拟设备被独占/后端拒绝
        f.Vm.AudioDeviceIndex = 1;

        Expect(ref failures, f.Vm.AudioDeviceIndex == 2, $"切换失败应回滚到原索引 2,实际 {f.Vm.AudioDeviceIndex}");
        Expect(ref failures, f.Player.OutputDeviceId == BtId, "切换失败应保持原设备不变");
        Expect(ref failures, f.Vm.AudioDeviceHint?.Contains("失败") == true, "切换失败应给出提示");

        var reloaded = new AppStateStore(Path.Combine(dir, "state.json"));
        Expect(ref failures, reloaded.AudioOutputDeviceId == BtId, "失败的切换不应写盘");
        return failures;
    }

    /// <summary>后端不支持 ⇒ 隐藏下拉 + 说明文案,不产生任何设备调用。</summary>
    private static int UnsupportedBackendHidesPicker(string dir)
    {
        var failures = 0;
        var f = Create(dir, null, null, Speaker());

        Expect(ref failures, f.Vm.AudioDeviceSupported, "用例前提:桩默认支持设备选择");
        Expect(ref failures, f.Player.SetDeviceCallCount == 0, "构造时未选过设备不应调用应用接口");

        var dir2 = Path.Combine(dir, "unsupported");
        var statePath = Path.Combine(dir2, "state.json");
        var state = new AppStateStore(statePath);
        var player = new HeadlessApp.StubAudioPlayer { DeviceSelectionSupported = false };
        player.SetOutputDevices(Speaker());
        var vm = new SettingsViewModel(state, NewCache(dir2), player);
        vm.AudioDeviceRestoreTask?.Wait(TimeSpan.FromSeconds(5));

        Expect(ref failures, vm.AudioDeviceSupported == false, "后端不支持时 AudioDeviceSupported 应为 false");
        Expect(ref failures, vm.HasAudioDeviceHint, "不支持时应给出说明文案");
        Expect(ref failures, vm.AudioDeviceOptions.Count == 1 && vm.AudioDeviceOptions[0] == DefaultOption,
            "不支持时下拉只保留默认项(视图里整行隐藏)");
        Expect(ref failures, player.SetDeviceCallCount == 0, "不支持的后端不应被调用应用接口");
        return failures;
    }

    /// <summary>设置页"刷新"重扫(中途插上耳机)⇒ 列表更新但当前选择不被改掉。</summary>
    private static int RefreshKeepsSelection(string dir)
    {
        var failures = 0;
        var f = Create(dir, BtId, BtName, Speaker(), Bluetooth());
        Expect(ref failures, f.Vm.AudioDeviceIndex == 2, "用例前提:应从蓝牙耳机恢复");

        f.Player.SetOutputDevices(Speaker(), Bluetooth(), new AudioOutputDevice("dev-usb", "USB 声卡", false));
        f.Vm.RefreshAudioDevicesCommand.Execute(null);

        Expect(ref failures, f.Vm.AudioDeviceOptions.Count == 4, $"重扫后下拉应有 4 项,实际 {f.Vm.AudioDeviceOptions.Count}");
        Expect(ref failures, f.Vm.AudioDeviceIndex == 2, $"重扫不应改变当前选择,实际 {f.Vm.AudioDeviceIndex}");
        Expect(ref failures, f.Player.OutputDeviceId == BtId, "重扫不应重新应用设备");
        Expect(ref failures, f.Vm.AudioDeviceHint is null, $"重扫成功不应留下提示,实际「{f.Vm.AudioDeviceHint}」");
        return failures;
    }

    // ---- 夹具 ----

    private static AudioOutputDevice Speaker() => new(SpeakerId, SpeakerName, true);

    private static AudioOutputDevice Bluetooth() => new(BtId, BtName, false);

    private static MusicCacheService NewCache(string dir) =>
        new(64, Path.Combine(dir, "cache"), new HttpClient());

    /// <summary>按"用户上次的选择"(可为空)预置 state.json,再用它新建一套 VM + 桩播放器。</summary>
    private static (SettingsViewModel Vm, HeadlessApp.StubAudioPlayer Player, AppStateStore State) Create(
        string dir, string? savedId, string? savedName, params AudioOutputDevice[] devices)
    {
        Directory.CreateDirectory(dir);
        var statePath = Path.Combine(dir, "state.json");
        if (savedId is not null || savedName is not null)
        {
            var seed = new AppStateStore(statePath);
            seed.AudioOutputDeviceId = savedId;
            seed.AudioOutputDeviceName = savedName;
            seed.Save();
        }

        var state = new AppStateStore(statePath);
        var player = new HeadlessApp.StubAudioPlayer();
        player.SetOutputDevices(devices);
        var vm = new SettingsViewModel(state, NewCache(dir), player);
        vm.AudioDeviceRestoreTask?.Wait(TimeSpan.FromSeconds(5)); // 桩是同步完成的,这里只是兜底
        return (vm, player, state);
    }

    private static void Expect(ref int failures, bool condition, string message)
    {
        if (condition) return;
        failures++;
        Console.Error.WriteLine($"[audiodevice][FAIL] {message}");
    }
}
