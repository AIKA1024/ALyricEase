namespace ALyricEase.Services;

/// <summary>音乐源标识。Song.Source 决定播放地址/歌词等请求经 MusicApiProvider 路由到哪个 IMusicApi 实现。</summary>
public enum MusicSource
{
    NetEase = 0,
    QQ = 1,
}
