namespace ALyricEase.Models;

/// <summary>首页推荐卡片:封面 + 标题 + 副标题。</summary>
public sealed record RecommendItem(long Id, string Title, string Subtitle, string CoverUrl);
