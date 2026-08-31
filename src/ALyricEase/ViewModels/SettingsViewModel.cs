using System.Diagnostics;
using System.IO;
using ALyricEase.Services;
using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ALyricEase.ViewModels;

/// <summary>开源项目引用条目(名称 + 仓库地址;记录类型供 XAML 编译绑定 DataTemplate 使用)。</summary>
public sealed record OpenSourceProject(string Name, string Url);

/// <summary>设置页(参考网易云 UWP 设置:外观/交互/播放/网络/存储/关于分区卡片)。
/// 偏好项直接落在 AppStateStore(state.json);主题改动即时生效(RequestedThemeVariant)。
/// 音频输出设备/代理/快捷键等依赖后端能力的入口当前为占位,接入时替换对应 Command 即可。</summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly AppStateStore _state;

    /// <summary>操作结果反馈(清除缓存/占位入口提示),显示在存储分区下方。</summary>
    [ObservableProperty] private string? _status;

    public SettingsViewModel(AppStateStore state)
    {
        _state = state;
        ApplyTheme(_state.Theme); // 启动恢复已保存的主题
    }

    // ---- 下拉框选项 ----

    public IReadOnlyList<string> ThemeOptions { get; } = ["使用系统设置", "浅色", "深色"];

    public IReadOnlyList<string> LanguageOptions { get; } = ["匹配系统设置(默认)", "简体中文"];

    public IReadOnlyList<string> PerformanceOptions { get; } = ["平衡(默认)", "最佳性能", "最佳质量"];

    public IReadOnlyList<string> AudioQualityOptions { get; } = ["标准", "较高", "极高", "无损(仅VIP可用)"];

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
        set { _state.DynamicBackground = value; _state.Save(); OnPropertyChanged(nameof(DynamicBackground)); }
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

    /// <summary>音频质量档位(当前仅存储偏好,播放器选流尚未接入)。</summary>
    public int AudioQualityIndex
    {
        get => _state.AudioQuality;
        set { _state.AudioQuality = value; _state.Save(); OnPropertyChanged(nameof(AudioQualityIndex)); }
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

    // ---- 占位入口(后端能力接入后替换实现) ----

    /// <summary>音频输出设备选择(当前使用系统默认设备)。</summary>
    [RelayCommand]
    private void SelectAudioDevice() => Status = "音频输出设备选择即将支持,当前使用系统默认设备";

    /// <summary>键盘快捷键编辑。</summary>
    [RelayCommand]
    private void EditHotkeys() => Status = "快捷键自定义即将支持";

    /// <summary>应用网络代理设置。</summary>
    [RelayCommand]
    private void OpenProxySettings() => Status = "网络代理设置即将支持,当前使用系统代理";

    // ---- 存储 ----

    /// <summary>清除数据缓存:删除应用数据目录下的 cache 目录(封面/音频缓存未来落在这里)。</summary>
    [RelayCommand]
    private void ClearCache()
    {
        try
        {
            var cacheDir = Path.Combine(
#if ANDROID
                global::Android.App.Application.Context.FilesDir!.AbsolutePath,
#else
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
#endif
                "ALyricEase", "cache");
            if (Directory.Exists(cacheDir))
            {
                Directory.Delete(cacheDir, recursive: true);
            }

            Status = "已清除数据缓存";
        }
        catch
        {
            Status = "清除缓存失败,请重试";
        }
    }

    // ---- 关于:开源项目引用 ----

    /// <summary>开源项目引用列表展开/收起(默认展开,与原版一致)。</summary>
    [ObservableProperty] private bool _openSourceExpanded = true;

    /// <summary>开合开源项目引用列表。</summary>
    [RelayCommand]
    private void ToggleOpenSource() => OpenSourceExpanded = !OpenSourceExpanded;

    /// <summary>开源项目引用(名称 + 仓库地址)。</summary>
    public IReadOnlyList<OpenSourceProject> OpenSourceProjects { get; } =
    [
        new("Binaryify/NeteaseCloudMusicApi", "https://github.com/Binaryify/NeteaseCloudMusicApi"),
        new("Fluent UI System Icons (MIT)", "https://github.com/microsoft/fluentui-system-icons"),
        new("Avalonia UI (MIT)", "https://github.com/AvaloniaUI/Avalonia"),
    ];

    /// <summary>在系统浏览器打开开源项目主页(桌面端;Android 端后续接 Intent)。</summary>
    [RelayCommand]
    private void OpenProject(OpenSourceProject? project)
    {
        if (project is null) return;
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
