namespace ALyricEase.Models;

/// <summary>浏览页榜单卡片(两个音源共用的领域形态,不是上游响应)。
///
/// <para>
/// <see cref="Id"/> 的含义随 <see cref="Source"/> 变化:网易云的榜单本身就是官方歌单,Id 是歌单 id,
/// 直接进歌单页;<b>QQ 的 Id 是榜单编号 topId</b>(26 热歌 / 27 新歌 / 62 飙升…),不是歌单 disstid,
/// 打开时要交给 <c>Playlist.TopId</c> 走榜单曲目接口。
/// </para>
/// </summary>
public sealed record ToplistItem(
    long Id,
    string Name,
    string CoverUrl,
    string UpdateText,
    int TrackCount,
    long PlayCount,
    Services.MusicSource Source)
{
    /// <summary>榜单导语(网易云 description / QQ intro)。卡片用不到,浏览页顶部海报位拿它当一句介绍;
    /// 两边都可能给空串,由海报位补兜底文案。</summary>
    public string Description { get; init; } = "";

    /// <summary>卡片副标题:「每日更新 · 100 首」;没有更新文案时只留曲数。</summary>
    public string Summary => UpdateText.Length > 0
        ? $"{UpdateText} · {TrackCount} 首"
        : $"{TrackCount} 首";
}
