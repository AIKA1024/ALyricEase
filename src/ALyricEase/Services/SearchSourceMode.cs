namespace ALyricEase.Services;

/// <summary>搜索页来源选择。与双值 MusicSource 分开，因为综合搜索是独立的第三种模式。</summary>
public enum SearchSourceMode
{
    Combined = 0,
    NetEase = 1,
    QQ = 2,
}
