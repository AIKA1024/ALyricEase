using ALyricEase.Services;

namespace ALyricEase.Models;

/// <summary>搜索类型(结果页 Tab / 综合搜索分区;与原版 SearchResultView 的 All/Track/Album/Artist/Playlist/User 对应)。</summary>
public enum SearchKind
{
    All,
    Track,
    Album,
    Artist,
    Playlist,
    User,
}

/// <summary>综合搜索结果:各类型分区一次拿回(All 用全部分区,单类型只填对应列表)。
/// 不支持多类型搜索的音源返回 null(UI 退化为仅歌曲)。</summary>
public sealed class SearchAllResult
{
    public List<Song> Songs { get; } = new();

    public List<SearchPlaylistItem> Playlists { get; } = new();

    public List<SearchArtistItem> Artists { get; } = new();

    public List<SearchAlbumItem> Albums { get; } = new();

    public List<SearchUserItem> Users { get; } = new();
}

/// <summary>搜索结果里的歌单项(QQ dissid / 网易云歌单 id 均入 Id,Source 区分路由)。</summary>
public sealed class SearchPlaylistItem
{
    public long Id { get; init; }

    public MusicSource Source { get; init; } = MusicSource.NetEase;

    public string Name { get; init; } = "";

    public string CoverUrl { get; init; } = "";

    public string Creator { get; init; } = "";

    public int TrackCount { get; init; }
}

/// <summary>搜索结果里的歌手项(QQ 用 Mid,网易云用 Id)。</summary>
public sealed class SearchArtistItem
{
    public long Id { get; init; }

    public string Mid { get; init; } = "";

    public MusicSource Source { get; init; } = MusicSource.NetEase;

    public string Name { get; init; } = "";

    public string AvatarUrl { get; init; } = "";
}

/// <summary>搜索结果里的专辑项(QQ 用 Mid,网易云用 Id)。</summary>
public sealed class SearchAlbumItem
{
    public long Id { get; init; }

    public string Mid { get; init; } = "";

    public MusicSource Source { get; init; } = MusicSource.NetEase;

    public string Name { get; init; } = "";

    public string CoverUrl { get; init; } = "";

    /// <summary>发行日期文本(网易云 yyyy-M-d;QQ 可能缺失,缺失时副标题只显示歌手)。</summary>
    public string PublishDateText { get; init; } = "";

    /// <summary>曲目数;0 = 未知(不显示"共 N 首歌")。</summary>
    public int SongCount { get; init; }

    public string ArtistName { get; init; } = "";
}

/// <summary>搜索结果里的用户项(仅网易云;本移植无用户主页,行仅展示)。</summary>
public sealed class SearchUserItem
{
    public long Id { get; init; }

    public string Name { get; init; } = "";

    public string AvatarUrl { get; init; } = "";

    public string Signature { get; init; } = "";
}
