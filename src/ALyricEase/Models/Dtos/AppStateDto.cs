using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ALyricEase.Models.Dtos;

/// <summary>应用界面状态(state.json)的 JSON 源生成上下文(NativeAOT 兼容,与 CookieFile 同模式)。
/// 可空字段区分"从未记录过"与已记录值;加载端对 null 回退默认。</summary>
[JsonSerializable(typeof(AppStateFile))]
internal sealed partial class AppStateJsonContext : JsonSerializerContext
{
}

/// <summary>state.json 的落盘形态(字段全部可空)。</summary>
public sealed class AppStateFile
{
    public bool? NetEaseGroupExpanded { get; set; }

    public bool? QqGroupExpanded { get; set; }

    /// <summary>聚合歌单列表(用户经侧栏"+"创建;null = 从未创建过)。</summary>
    public List<AggregatePlaylistFile>? AggregatePlaylists { get; set; }

    /// <summary>搜索历史(最新在前;null = 从未搜过)。</summary>
    public List<string>? SearchHistory { get; set; }

    public double? WindowWidth { get; set; }

    public double? WindowHeight { get; set; }

    public int? WindowX { get; set; }

    public int? WindowY { get; set; }

    public bool? WindowMaximized { get; set; }

    // ---- 设置页偏好(SettingsViewModel 读写;null = 用户未改过,加载端回退默认) ----

    /// <summary>主题:System/Light/Dark。</summary>
    public string? Theme { get; set; }

    /// <summary>界面语言:System/zh-CN(当前仅存储偏好,本地化尚未接入)。</summary>
    public string? Language { get; set; }

    /// <summary>性能与体验:Balanced/Quality(当前仅存储偏好)。</summary>
    public string? PerformanceMode { get; set; }

    /// <summary>播放详情页动态背景效果。</summary>
    public bool? DynamicBackground { get; set; }

    /// <summary>兼容的视觉效果(低端设备关掉重动效)。</summary>
    public bool? CompatibilityVisual { get; set; }

    /// <summary>音频质量档位(0=标准 1=较高 2=极高 3=无损;当前仅存储偏好)。</summary>
    public int? AudioQuality { get; set; }

    /// <summary>传统播放控制(点击行直接播放/双击入队等老式行为,当前仅存储偏好)。</summary>
    public bool? LegacyPlaybackControl { get; set; }

    /// <summary>音频交叉淡化开关。</summary>
    public bool? Crossfade { get; set; }

    /// <summary>交叉淡化时长(秒)。</summary>
    public double? CrossfadeSeconds { get; set; }

    /// <summary>播放模式(0=列表循环 1=单曲循环 2=随机播放)。</summary>
    public int? PlaybackMode { get; set; }

    /// <summary>播放器音量(0-100)。</summary>
    public int? Volume { get; set; }
}

/// <summary>聚合歌单落盘形态。</summary>
public sealed class AggregatePlaylistFile
{
    public string? Id { get; set; }

    public string? Name { get; set; }

    /// <summary>成员按来源排列顺序(AggregateSourceOrder 枚举值;null = 默认网易云在前)。</summary>
    public int? SourceOrder { get; set; }

    public List<AggregateMemberFile>? Members { get; set; }
}

/// <summary>聚合歌单成员落盘形态(Source 为 MusicSource 枚举值)。</summary>
public sealed class AggregateMemberFile
{
    public int? Source { get; set; }

    public long? PlaylistId { get; set; }

    public string? PlaylistName { get; set; }
}
