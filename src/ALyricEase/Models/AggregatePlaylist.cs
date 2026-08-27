using System.Collections.Generic;

namespace ALyricEase.Models;

/// <summary>聚合歌单成员按来源的排列顺序(打开时按此分组合并,组内保持成员原序)。</summary>
public enum AggregateSourceOrder
{
    /// <summary>先网易云,后 QQ。</summary>
    NetEaseFirst = 0,

    /// <summary>先 QQ,后网易云。</summary>
    QqFirst = 1,
}

/// <summary>聚合歌单:把网易云/QQ 音乐若干用户歌单合并为一个侧栏入口,
/// 打开时按 SourceOrder 依次拉取各成员歌单曲目合并展示。成员按 源+歌单 id 引用,
/// 名称仅作展示兜底(源未登录时仍显示成员名)。持久化于 state.json。</summary>
public sealed class AggregatePlaylist
{
    public string Id { get; init; } = "";

    public string Name { get; init; } = "";

    /// <summary>成员按来源的排列顺序(可经聚合歌单设置弹窗修改)。</summary>
    public AggregateSourceOrder SourceOrder { get; set; } = AggregateSourceOrder.NetEaseFirst;

    public List<AggregatePlaylistMember> Members { get; init; } = new();
}

/// <summary>聚合歌单成员:引用一个具体音源的用户歌单。</summary>
public sealed class AggregatePlaylistMember
{
    public Services.MusicSource Source { get; init; }

    public long PlaylistId { get; init; }

    public string PlaylistName { get; init; } = "";
}
