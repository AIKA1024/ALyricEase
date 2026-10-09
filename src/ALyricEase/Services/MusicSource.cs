namespace ALyricEase.Services;

/// <summary>音乐源标识。Song.Source 决定播放地址/歌词等请求经 MusicApiProvider 路由到哪个 IMusicApi 实现。
/// Local 不是在线音源:没有注册任何 IMusicApi,播放直接走 Song.LocalFilePath 的本地文件,
/// 歌词走同名 .lrc;Resolve(Local) 抛 NotSupportedException 属预期,调用方各自短路。</summary>
public enum MusicSource
{
    NetEase = 0,
    QQ = 1,
    Local = 2,
}
