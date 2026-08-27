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

    public double? WindowWidth { get; set; }

    public double? WindowHeight { get; set; }

    public int? WindowX { get; set; }

    public int? WindowY { get; set; }

    public bool? WindowMaximized { get; set; }
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
