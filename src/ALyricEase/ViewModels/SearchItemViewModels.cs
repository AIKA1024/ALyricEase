using ALyricEase.Models;
using ALyricEase.Services;

namespace ALyricEase.ViewModels;

/// <summary>搜索结果页分区行 VM(歌单/表演者/专辑/用户)。封面 URL 由视图租约式加载;
/// 点击行为由视图绑定 MainViewModel 的 Open*Command 完成(与 SongItemViewModel 同一 ServiceLocator 模式)。</summary>
public abstract class SearchItemViewModelBase : ViewModelBase
{
    public string CoverUrl { get; protected init; } = "";
}

/// <summary>歌单分区行:点击经 MainViewModel.OpenShellPlaylist/OpenShellQqPlaylist 打开歌单详情。</summary>
public sealed class SearchPlaylistItemViewModel : SearchItemViewModelBase
{
    public SearchPlaylistItemViewModel(SearchPlaylistItem item)
    {
        Item = item;
        CoverUrl = item.CoverUrl;
    }

    public SearchPlaylistItem Item { get; }

    public string Name => Item.Name;

    /// <summary>副标题:"creator 创建 · 共 N 首歌"(对齐原版;creator 缺失时只显示歌数)。</summary>
    public string Subtitle => Item.Creator.Length > 0
        ? $"{Item.Creator} 创建 · 共 {Item.TrackCount} 首歌"
        : $"共 {Item.TrackCount} 首歌";

    public bool IsQq => Item.Source == MusicSource.QQ;
}

/// <summary>表演者分区行:点击经 MainViewModel.OpenArtist/OpenQqArtist 打开歌手页。</summary>
public sealed class SearchArtistItemViewModel : SearchItemViewModelBase
{
    public SearchArtistItemViewModel(SearchArtistItem item)
    {
        Item = item;
        CoverUrl = item.AvatarUrl;
    }

    public SearchArtistItem Item { get; }

    public string Name => Item.Name;

    public bool IsQq => Item.Source == MusicSource.QQ;
}

/// <summary>专辑分区行:点击经 MainViewModel.OpenAlbum/OpenQqAlbum 打开专辑页。
/// 副标题 "yyyy-M-d发布 · 共N首歌"(对齐原版;日期/曲数缺失时段省略)。</summary>
public sealed class SearchAlbumItemViewModel : SearchItemViewModelBase
{
    public SearchAlbumItemViewModel(SearchAlbumItem item)
    {
        Item = item;
        CoverUrl = item.CoverUrl;
    }

    public SearchAlbumItem Item { get; }

    public string Name => Item.Name;

    public string Subtitle
    {
        get
        {
            var date = Item.PublishDateText.Length > 0 ? $"{Item.PublishDateText}发布" : "";
            var count = Item.SongCount > 0 ? $"共 {Item.SongCount} 首歌" : "";
            return date.Length > 0 && count.Length > 0 ? $"{date} · {count}" : date + count;
        }
    }

    public bool IsQq => Item.Source == MusicSource.QQ;
}

/// <summary>用户分区行(仅网易云;本移植无用户主页,行仅展示不可点)。</summary>
public sealed class SearchUserItemViewModel : SearchItemViewModelBase
{
    public SearchUserItemViewModel(SearchUserItem item)
    {
        Item = item;
        CoverUrl = item.AvatarUrl;
    }

    public SearchUserItem Item { get; }

    public string Name => Item.Name;

    public string Subtitle => Item.Signature;
}
