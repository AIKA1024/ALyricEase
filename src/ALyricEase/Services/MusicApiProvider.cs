using ALyricEase.Models;

namespace ALyricEase.Services;

/// <summary>音源注册表:DI 注入全部 IMusicApi 实现,按 MusicSource(即 Song.Source)
/// 把播放地址/歌词等请求路由到对应客户端。新增音源 = 新实现 IMusicApi + 注册一行。</summary>
public sealed class MusicApiProvider
{
    private readonly IReadOnlyDictionary<MusicSource, IMusicApi> _apis;

    public MusicApiProvider(IEnumerable<IMusicApi> apis)
    {
        var all = apis.ToList();
        _apis = all.ToDictionary(a => a.Source);
        All = all.AsReadOnly();
    }

    /// <summary>全部已注册音源(UI 音源切换列表用,注册顺序)。</summary>
    public IReadOnlyList<IMusicApi> All { get; }

    /// <summary>默认音源:网易云(歌单/推荐/红心等账号功能的主曲库)。</summary>
    public IMusicApi Default => Resolve(MusicSource.NetEase);

    public IMusicApi Resolve(MusicSource source)
        => _apis.TryGetValue(source, out var api)
            ? api
            : throw new NotSupportedException($"未注册的音乐源:{source}");

    /// <summary>按歌曲来源路由。</summary>
    public IMusicApi Resolve(Song song) => Resolve(song.Source);
}
