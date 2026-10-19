namespace ALyricEase.Models;

/// <summary>QQ 他人主页数据(网页 profile 同源:主页 fcg + 收藏歌单 asset)。
/// 与"自己"的 LoadQqAsync 口径一致:喜欢 → 音乐品味,自建 → 参与创作,其余 → 收藏。</summary>
public sealed class QQUserHomepage
{
    public string Nickname { get; init; } = "";

    public string AvatarUrl { get; init; } = "";

    public int Follows { get; init; }

    public int Followeds { get; init; }

    /// <summary>音乐品味里的"喜欢"(可能没有公开)。</summary>
    public Playlist? Liked { get; init; }

    /// <summary>参与创作的歌单(mydiss.list)。</summary>
    public List<Playlist> Created { get; init; } = [];

    /// <summary>收藏的他人歌单(fcg_get_profile_order_asset,隐私账号可能为空)。</summary>
    public List<Playlist> Collected { get; init; } = [];
}
