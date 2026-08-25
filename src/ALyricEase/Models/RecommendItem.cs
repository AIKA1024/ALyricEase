namespace ALyricEase.Models;

/// <summary>首页推荐卡片:封面 + 标题 + 副标题 + 播放量(歌单卡片有,歌曲卡片为 0)。
/// 歌单卡片 Id=歌单 id(点击进歌单页),歌曲卡片 Id=歌曲 id(点击播放)。</summary>
public sealed record RecommendItem(long Id, string Title, string Subtitle, string CoverUrl, long PlayCount = 0, int TrackCount = 0);
