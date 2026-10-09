using ALyricEase.Services;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace ALyricEase.Infrastructure;

/// <summary>音源角标位图(网易云/QQ 的浏览器标签页 favicon):静态缓存按音源共享一份。
/// 个性推荐歌曲行(TrackRow)与个人主页歌单列表行共用;资源缺失返回 null(Image 空源不渲染)。</summary>
internal static class SourceBadges
{
    private static IImage? _netEase;
    private static IImage? _qq;

    public static IImage? For(MusicSource source) => source == MusicSource.QQ
        ? _qq ??= Load("avares://ALyricEase/Assets/TrackTags/SourceQQ.png")
        : _netEase ??= Load("avares://ALyricEase/Assets/TrackTags/SourceNetease.png");

    private static IImage? Load(string uri)
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri(uri));
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }
}
