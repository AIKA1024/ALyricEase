using System.Diagnostics;
using ALyricEase.Infrastructure;
using ALyricEase.Services;
using ALyricEase.Services.Audio;
using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ALyricEase.ViewModels;

/// <summary>开源项目引用条目(名称 + 仓库地址;记录类型供 XAML 编译绑定 DataTemplate 使用)。</summary>
public sealed record OpenSourceProject(string Name, string Url);

/// <summary>设置页(参考网易云 UWP 设置:外观/交互/播放/网络/存储/关于分区卡片)。
/// 偏好项直接落在 AppStateStore(state.json);主题改动即时生效(RequestedThemeVariant)。
/// 代理/快捷键等依赖后端能力的入口仍为占位,接入时替换对应 Command 即可。</summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    /// <summary>输出设备下拉的第 0 项:不指定设备 = 跟随系统默认。</summary>
    private const string DefaultDeviceOption = "跟随系统默认设备";

    private readonly AppStateStore _state;
    private readonly MusicCacheService _musicCache;
    private readonly IAudioPlayer _player;

    /// <summary>当前枚举到的输出设备(不含"跟随系统默认设备"伪项);索引 i 对应下拉第 i+1 项。</summary>
    private readonly List<AudioOutputDevice> _devices = new();

    private int _audioDeviceIndex;

    /// <summary>操作结果反馈(清除缓存/占位入口提示),显示在存储分区下方。</summary>
    [ObservableProperty] private string? _status;

    public SettingsViewModel(AppStateStore state, MusicCacheService musicCache, IAudioPlayer player)
    {
        _state = state;
        _musicCache = musicCache;
        _player = player;
        ApplyTheme(_state.Theme); // 启动恢复已保存的主题

        // 输出设备:启动就把上次选的设备贴回播放器(不必等用户打开设置页)。
        // 枚举是异步的,这里 fire-and-forget —— 失败/设备不在都只在设置页给出提示,不阻塞启动。
        AudioDeviceSupported = _player.SupportsOutputDeviceSelection;
        if (AudioDeviceSupported) AudioDeviceRestoreTask = RestoreAudioDeviceAsync();
        else AudioDeviceHint = "当前平台不支持切换输出设备,播放时使用系统默认设备";
    }

    // ---- 下拉框选项 ----

    public IReadOnlyList<string> ThemeOptions { get; } = ["使用系统设置", "浅色", "深色"];

    public IReadOnlyList<string> LanguageOptions { get; } = ["匹配系统设置(默认)", "简体中文"];

    public IReadOnlyList<string> PerformanceOptions { get; } = ["平衡(默认)", "最佳性能", "最佳质量"];

    public IReadOnlyList<string> AudioQualityOptions { get; } =
    [
        "标准",
        "较高",
        "极高",
        "无损(仅VIP可用)",
    ];

    // ---- 外观 ----

    /// <summary>主题:0=System 1=Light 2=Dark;切换即写回并应用。</summary>
    public int ThemeIndex
    {
        get => _state.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 };
        set
        {
            _state.Theme = value switch { 1 => "Light", 2 => "Dark", _ => "System" };
            ApplyTheme(_state.Theme);
            _state.Save();
            OnPropertyChanged(nameof(ThemeIndex));
        }
    }

    /// <summary>界面语言(当前仅存储偏好,本地化资源尚未接入)。</summary>
    public int LanguageIndex
    {
        get => _state.Language == "zh-CN" ? 1 : 0;
        set
        {
            _state.Language = value == 1 ? "zh-CN" : "System";
            _state.Save();
            OnPropertyChanged(nameof(LanguageIndex));
        }
    }

    /// <summary>性能与体验(当前仅存储偏好)。</summary>
    public int PerformanceIndex
    {
        get => _state.PerformanceMode switch { "Performance" => 1, "Quality" => 2, _ => 0 };
        set
        {
            _state.PerformanceMode = value switch { 1 => "Performance", 2 => "Quality", _ => "Balanced" };
            _state.Save();
            OnPropertyChanged(nameof(PerformanceIndex));
        }
    }

    /// <summary>播放详情界面的动态背景效果。</summary>
    public bool DynamicBackground
    {
        get => _state.DynamicBackground;
        set
        {
            _state.DynamicBackground = value;
            _state.Save();
            // 只改"怎么画"，不失效任何状态：通知正在显示的界面立刻按新设置改绘。
            _state.NotifyVisualEffectsChanged();
            OnPropertyChanged(nameof(DynamicBackground));
        }
    }

    /// <summary>兼容的视觉效果(低端设备减弱动效)。</summary>
    public bool CompatibilityVisual
    {
        get => _state.CompatibilityVisual;
        set
        {
            _state.CompatibilityVisual = value;
            _state.Save();
            OnPropertyChanged(nameof(CompatibilityVisual));
            OnPropertyChanged(nameof(CompatibilityVisualText));
        }
    }

    public string CompatibilityVisualText => CompatibilityVisual ? "已启用" : "未启用";

    partial void OnStatusChanged(string? value) => OnPropertyChanged(nameof(HasStatus));

    public bool HasStatus => !string.IsNullOrEmpty(Status);

    // ---- 播放 ----

    /// <summary>统一音频质量档位；播放时按歌曲来源映射到网易云或 QQ 的对应档位。</summary>
    public int AudioQualityIndex
    {
        get => _state.AudioQuality;
        set
        {
            _state.AudioQuality = AudioQualityMapper.NormalizeIndex(value);
            _state.Save();
            OnPropertyChanged(nameof(AudioQualityIndex));
        }
    }

    /// <summary>传统播放控制。</summary>
    public bool LegacyPlaybackControl
    {
        get => _state.LegacyPlaybackControl;
        set
        {
            _state.LegacyPlaybackControl = value;
            _state.Save();
            OnPropertyChanged(nameof(LegacyPlaybackControl));
            OnPropertyChanged(nameof(LegacyPlaybackControlText));
        }
    }

    public string LegacyPlaybackControlText => LegacyPlaybackControl ? "已启用" : "未启用";

    /// <summary>音频交叉淡化开关。</summary>
    public bool Crossfade
    {
        get => _state.Crossfade;
        set
        {
            _state.Crossfade = value;
            _state.Save();
            OnPropertyChanged(nameof(Crossfade));
            OnPropertyChanged(nameof(CrossfadeText));
        }
    }

    public string CrossfadeText => Crossfade ? "已启用" : "未启用";

    /// <summary>交叉淡化时长(秒)；滑块停止变化 400ms 后合并保存。</summary>
    public double CrossfadeSeconds
    {
        get => _state.CrossfadeSeconds;
        set
        {
            _state.CrossfadeSeconds = Math.Round(value, 1);
            _state.ScheduleSave();
            OnPropertyChanged(nameof(CrossfadeSeconds));
            OnPropertyChanged(nameof(CrossfadeSecondsText));
        }
    }

    public string CrossfadeSecondsText => $"{CrossfadeSeconds:0.#} 秒";

    // ---- 主题应用 ----

    private static void ApplyTheme(string theme)
    {
        try
        {
            if (Application.Current is not { } app) return;
            app.RequestedThemeVariant = theme switch
            {
                "Light" => ThemeVariant.Light,
                "Dark" => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };
        }
        catch
        {
            // 无 Application 的上下文(部分无头探测)忽略
        }
    }

    // ---- 播放:音频输出设备 ----

    /// <summary>下拉项:第 0 项恒为"跟随系统默认设备",其后为枚举到的设备名(系统默认那台加标注)。</summary>
    [ObservableProperty] private IReadOnlyList<string> _audioDeviceOptions = [DefaultDeviceOption];

    /// <summary>设备相关提示(不支持/设备未连接/切换失败),空则整行隐藏。</summary>
    [ObservableProperty] private string? _audioDeviceHint;

    /// <summary>后端是否支持切换输出设备(Android 无头/老系统为 false,此时下拉整个隐藏)。</summary>
    [ObservableProperty] private bool _audioDeviceSupported;

    partial void OnAudioDeviceHintChanged(string? value) => OnPropertyChanged(nameof(HasAudioDeviceHint));

    public bool HasAudioDeviceHint => !string.IsNullOrEmpty(AudioDeviceHint);

    /// <summary>选中的输出设备序号:0 = 跟随系统默认设备。写入即应用到播放器并落盘。</summary>
    public int AudioDeviceIndex
    {
        get => _audioDeviceIndex;
        set
        {
            if (value >= 0) ApplyAudioDevice(value); // ComboBox 清空选择(-1)时忽略
        }
    }

    /// <summary>枚举进行中(启动恢复与页面刷新会几乎同时发起,避免两边交叉重建列表)。</summary>
    private bool _enumeratingDevices;

    /// <summary>启动恢复设备偏好的任务(仅无头回归探针 --audiodevice 等待用,产品代码不读)。</summary>
    internal Task? AudioDeviceRestoreTask { get; private set; }

    /// <summary>重新扫描输出设备(设置页每次进入 + "刷新"按钮)。保持当前选择,只更新列表与提示。</summary>
    [RelayCommand]
    private async Task RefreshAudioDevicesAsync()
    {
        if (!AudioDeviceSupported || _enumeratingDevices) return;

        _enumeratingDevices = true;
        try
        {
            await EnumerateAsync();
        }
        catch
        {
            AudioDeviceHint = "读取音频输出设备失败,当前使用系统默认设备";
            return;
        }
        finally
        {
            _enumeratingDevices = false;
        }

        AudioDeviceHint = null; // 先清掉上一轮的提示,再按最新列表重新判定

        // 选择以播放器回读的当前设备为准;播放器还没指定过设备时,用上次保存的偏好补上
        // (场景:启动时那台设备还没插上,用户后来才插上再打开设置页)。
        var index = IndexOfCurrentDevice();
        if (index == 0 && _player.OutputDeviceId is null && _state.AudioOutputDeviceId is { Length: > 0 })
        {
            var saved = ResolveSavedDevice();
            if (saved > 0 && _player.TrySetOutputDevice(_devices[saved - 1].Id)) index = saved;
        }

        SetAudioDeviceIndexSilently(index);
        UpdateAudioDeviceHint(index);
    }

    /// <summary>启动恢复:枚举 → 匹配上次选的设备 → 贴到播放器。匹配不上就回退系统默认并说明原因。</summary>
    private async Task RestoreAudioDeviceAsync()
    {
        if (_enumeratingDevices) return;
        _enumeratingDevices = true;
        try
        {
            await EnumerateAsync();
        }
        catch
        {
            AudioDeviceHint = "读取音频输出设备失败,当前使用系统默认设备";
            return;
        }
        finally
        {
            _enumeratingDevices = false;
        }

        if (_state.AudioOutputDeviceId is not { Length: > 0 })
        {
            // 从未选过设备(或已显式选了系统默认):保持跟随系统默认
            SetAudioDeviceIndexSilently(0);
            return;
        }

        var index = ResolveSavedDevice();
        if (index == 0)
        {
            // 设备已保存但当前不在:不写回 state(用户可能只是暂时拔掉),只回退本次会话
            SetAudioDeviceIndexSilently(0);
            AudioDeviceHint = string.IsNullOrEmpty(_state.AudioOutputDeviceName)
                ? "上次选择的输出设备已不可用,已切回系统默认设备"
                : $"上次选择的「{_state.AudioOutputDeviceName}」当前不可用,已切回系统默认设备";
            return;
        }

        var device = _devices[index - 1];
        if (!_player.TrySetOutputDevice(device.Id))
        {
            SetAudioDeviceIndexSilently(0);
            AudioDeviceHint = $"切换到「{device.Name}」失败,当前使用系统默认设备";
            return;
        }

        SetAudioDeviceIndexSilently(index);
        // 名字匹配成功(Id 变了)时把新 Id 写回,免得下次还要再靠名字找
        if (_state.AudioOutputDeviceId != device.Id)
        {
            _state.AudioOutputDeviceId = device.Id;
            _state.AudioOutputDeviceName = device.Name;
            _state.Save();
        }
    }

    /// <summary>用户在下拉里选设备:先落到播放器,失败就回滚显示(不留"选了却没生效"的假象)。</summary>
    private void ApplyAudioDevice(int index)
    {
        if (index < 0 || index > _devices.Count) return; // 越界:忽略本次变更

        var device = index == 0 ? null : _devices[index - 1];
        var targetId = device?.Id;
        if (targetId == _player.OutputDeviceId && index == _audioDeviceIndex) return; // 无变化

        if (!_player.TrySetOutputDevice(targetId))
        {
            AudioDeviceHint = device is null
                ? "切回系统默认设备失败,已保持原设备"
                : $"切换到「{device.Name}」失败,已保持原设备";
            OnPropertyChanged(nameof(AudioDeviceIndex)); // 下拉回到实际生效的项
            return;
        }

        _audioDeviceIndex = index;
        _state.AudioOutputDeviceId = targetId;
        _state.AudioOutputDeviceName = device?.Name;
        _state.Save();
        AudioDeviceHint = null;
        OnPropertyChanged(nameof(AudioDeviceIndex));
    }

    /// <summary>枚举设备并重建下拉项(不改变当前选择)。</summary>
    private async Task EnumerateAsync()
    {
        var devices = await _player.RefreshOutputDevicesAsync();
        _devices.Clear();
        foreach (var device in devices)
            if (!string.IsNullOrEmpty(device.Id))
                _devices.Add(device);

        var options = new List<string>(_devices.Count + 1) { DefaultDeviceOption };
        options.AddRange(_devices.Select(FormatDevice));
        AudioDeviceOptions = options;
    }

    /// <summary>播放器当前生效的设备在 _devices 里的序号(1 起);未指定设备返回 0。</summary>
    private int IndexOfCurrentDevice()
    {
        var current = _player.OutputDeviceId;
        if (string.IsNullOrEmpty(current)) return 0;
        for (var i = 0; i < _devices.Count; i++)
            if (_devices[i].Id == current)
                return i + 1;
        return 0;
    }

    /// <summary>把落盘保存的设备偏好映射回下拉序号:先按 Id,再按展示名
    /// (Windows 换 USB 口/Android 重启后 Id 会变,名字才是用户认得出的锚点)。</summary>
    private int ResolveSavedDevice()
    {
        var savedId = _state.AudioOutputDeviceId;
        var savedName = _state.AudioOutputDeviceName;

        if (savedId is { Length: > 0 })
            for (var i = 0; i < _devices.Count; i++)
                if (_devices[i].Id == savedId)
                    return i + 1;

        if (!string.IsNullOrEmpty(savedName))
            for (var i = 0; i < _devices.Count; i++)
                if (string.Equals(_devices[i].Name, savedName, StringComparison.Ordinal))
                    return i + 1;

        return 0;
    }

    /// <summary>只改显示用的序号(不触发应用/落盘)。</summary>
    private void SetAudioDeviceIndexSilently(int index)
    {
        if (_audioDeviceIndex == index) return;
        _audioDeviceIndex = index;
        OnPropertyChanged(nameof(AudioDeviceIndex));
    }

    private void UpdateAudioDeviceHint(int index)
    {
        if (AudioDeviceHint is not null) return; // 保留更具体的失败原因
        AudioDeviceHint = index == 0 && _state.AudioOutputDeviceId is { Length: > 0 }
            ? $"上次选择的「{_state.AudioOutputDeviceName}」当前不可用,已切回系统默认设备"
            : null;
    }

    private static string FormatDevice(AudioOutputDevice device) =>
        device.IsDefault ? $"{device.Name}（系统默认）" : device.Name;

    // ---- 占位入口(后端能力接入后替换实现) ----

    /// <summary>键盘快捷键编辑。</summary>
    [RelayCommand]
    private void EditHotkeys() => Status = "快捷键自定义即将支持";

    /// <summary>应用网络代理设置。</summary>
    [RelayCommand]
    private void OpenProxySettings() => Status = "网络代理设置即将支持,当前使用系统代理";

    // ---- 存储 ----

    /// <summary>音乐、封面和歌词的统一磁盘缓存上限(MB)。</summary>
    public decimal MusicCacheMaximumSizeMb
    {
        get => _state.MusicCacheMaximumSizeMb;
        set
        {
            var normalized = MusicCacheService.NormalizeMaximumSizeMb(
                (int)Math.Round(value, MidpointRounding.AwayFromZero));
            if (_state.MusicCacheMaximumSizeMb == normalized) return;

            _state.MusicCacheMaximumSizeMb = normalized;
            _state.Save();
            OnPropertyChanged(nameof(MusicCacheMaximumSizeMb));
            _ = _musicCache.SetMaximumSizeMbAsync(normalized);
        }
    }

    /// <summary>清除音乐、封面、歌词和离线歌单索引。</summary>
    [RelayCommand]
    private async Task ClearCacheAsync()
    {
        try
        {
            await _musicCache.ClearAsync();
            CoverImagePipeline.ClearMemoryCache();
            CoverLoader.ClearMemoryCache();
            Status = "已清除缓存";
        }
        catch
        {
            Status = "清除缓存失败,请重试";
        }
    }

    // ---- 关于:开源项目引用 ----

    /// <summary>开源项目引用列表展开状态(默认展开;内置 Expander 的 IsExpanded 双向绑定,
    /// 之前手搓"按钮+MaxHeight 过渡"的仿 Expander 已替换为真 Expander)。</summary>
    [ObservableProperty] private bool _openSourceExpanded = true;

    /// <summary>开源项目引用(名称 + 仓库地址)。</summary>
    public IReadOnlyList<OpenSourceProject> OpenSourceProjects { get; } =
    [
        new("Binaryify/NeteaseCloudMusicApi", "https://github.com/Binaryify/NeteaseCloudMusicApi"),
        new("Fluent UI System Icons (MIT)", "https://github.com/microsoft/fluentui-system-icons"),
        new("Avalonia UI (MIT)", "https://github.com/AvaloniaUI/Avalonia"),
        new("AsyncImageLoader.Avalonia (MIT)", "https://github.com/AvaloniaUtils/AsyncImageLoader.Avalonia"),
    ];

    /// <summary>在系统浏览器打开开源项目主页(桌面端;Android 端后续接 Intent)。</summary>
    [RelayCommand]
    private void OpenProject(OpenSourceProject? project)
    {
        if (project is null) return;
        // ⚠️ #if ANDROID 在本程序集里是死分支(核心库只面向 net10.0),实际恒走 #else:
        // Android 上 Process.Start 会抛异常,落到 catch 里仍然把地址显示出来,行为可接受。
        // 真要区分平台请用 OperatingSystem.IsAndroid()。
#if ANDROID
        Status = project.Url;
#else
        try
        {
            Process.Start(new ProcessStartInfo(project.Url) { UseShellExecute = true });
        }
        catch
        {
            Status = project.Url; // 打开失败时至少把地址展示出来
        }
#endif
    }
}
