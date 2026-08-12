namespace ALyricEase.Models;

/// <summary>首页推荐卡片:封面 + 标题 + 副标题 + 播放量(歌单卡片有,歌曲卡片为 0)。</summary>
public sealed record RecommendItem(long Id, string Title, string Subtitle, string CoverUrl, long PlayCount = 0);
