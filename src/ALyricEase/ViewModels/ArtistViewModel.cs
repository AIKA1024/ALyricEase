using System.Collections.ObjectModel;
using System.Threading.Tasks;
using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services.NetEase;
using ALyricEase.Services.QQMusic;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ALyricEase.ViewModels;

/// <summary>歌手页:头部(圆形头像 + 名字)+ 热门歌曲横向网格 + 专辑/单曲卡片网格。从歌单行/专辑行歌手入口进入。
/// 网易云按数字 id、QQ 按 singer mid 取数(两套 Load)。</summary>
public sealed partial class ArtistViewModel : ViewModelBase
{
    private readonly NetEaseApiClient _api;
    private readonly QQMusicApiClient _qqApi;
    private readonly PlayerViewModel _player;

    public ArtistViewModel(NetEaseApiClient api, QQMusicApiClient qqApi, PlayerViewModel player)
    {
        _api = api;
        _qqApi = qqApi;
        _player = player;
    }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private bool _hasAlbums;
    [ObservableProperty] private bool _hasSingles;
    [ObservableProperty] private IImage? _avatar;

    /// <summary>热门歌曲(横向换行网格,每行 400px TrackRow)。</summary>
    public ObservableCollection<SongItemViewModel> Songs { get; } = new();

    /// <summary>专辑(横向卡片)。</summary>
    public ObservableCollection<AlbumCardViewModel> Albums { get; } = new();

    /// <summary>单曲与EP(横向卡片)。</summary>
    public ObservableCollection<AlbumCardViewModel> Singles { get; } = new();

    /// <summary>进入歌手页时调用:先清掉上一位歌手的内容,再拉歌手信息 + 热门歌曲 + 专辑/单曲。
    /// 失败静默(页面停在空态,而不是上一位歌手的数据)。</summary>
    public async Task LoadAsync(long artistId)
    {
        ClearContent();
        try
        {
            var info = await _api.GetArtistAsync(artistId);
            Name = info.Name;
            if (info.Avatar is { Length: > 0 } pic) Avatar = await CoverLoader.LoadAsync(pic, 240);

            var songs = await _api.GetArtistSongsAsync(artistId, 30);
            Subtitle = $"{songs.Count} 首单曲";
            var queue = songs;
            foreach (var s in songs)
                Songs.Add(new SongItemViewModel(s, _player.PlayFromList, api: _api, queue: queue, source: info.Name));

            var albums = await _api.GetArtistAlbumsAsync(artistId, 50);
            foreach (var a in albums)
            {
                var card = new AlbumCardViewModel(a.Id, a.Name, a.PicUrl);
                // 接口返回的 type 是字符串:专辑 / Single / EP
                if (string.Equals(a.Type, "专辑", StringComparison.Ordinal)) Albums.Add(card);
                else Singles.Add(card);
            }
            HasAlbums = Albums.Count > 0;
            HasSingles = Singles.Count > 0;
        }
        catch
        {
            // 网络失败静默:停在空态,不崩
        }
    }

    [RelayCommand]
    private async Task PlayAllAsync()
    {
        if (Songs.Count == 0) return;
        await Songs[0].PlayCommand.ExecuteAsync(null);
    }

    /// <summary>QQ 音乐歌手页(按 singer mid):头像用 T001 图床模板;名字从命中 mid 的曲目取;
    /// 专辑/单曲按 albumType 粗分(EP/单曲 → 单曲与EP,其余 → 专辑)。失败静默。</summary>
    public async Task LoadQqAsync(string singerMid)
    {
        ClearContent();
        try
        {
            Avatar = await CoverLoader.LoadAsync(
                $"https://y.gtimg.cn/music/photo_new/T001R300x300M000{singerMid}.jpg", 240);

            var songs = await _qqApi.GetArtistSongsAsync(singerMid, 30);
            Name = ResolveSingerName(songs, singerMid);
            Subtitle = $"{songs.Count} 首单曲";
            foreach (var s in songs)
                Songs.Add(new SongItemViewModel(s, _player.PlayFromList, queue: songs, source: Name));

            var albums = await _qqApi.GetArtistAlbumsAsync(singerMid, 50);
            foreach (var a in albums)
            {
                var card = new AlbumCardViewModel(a.Id, a.Name, a.PicUrl, a.Mid);
                // QQ albumType:录音室专辑/现场专辑等归专辑,EP/单曲归"单曲与EP"
                var isSingle = a.Type.Contains("EP", StringComparison.OrdinalIgnoreCase)
                               || a.Type.Contains("单曲", StringComparison.Ordinal);
                if (isSingle) Singles.Add(card);
                else Albums.Add(card);
            }
            HasAlbums = Albums.Count > 0;
            HasSingles = Singles.Count > 0;
        }
        catch
        {
            // 网络失败静默:停在空态,不崩
        }
    }

    /// <summary>清空上一位歌手的内容。歌手页每次进入都重建视图并立即绑定现有内容,
    /// 不清的话新页面会先渲染出上一位歌手的歌(加载失败时更会整页停在错位数据上)。</summary>
    private void ClearContent()
    {
        Name = "";
        Subtitle = "";
        Avatar = null;
        Songs.Clear();
        Albums.Clear();
        Singles.Clear();
        HasAlbums = false;
        HasSingles = false;
    }

    /// <summary>从热门歌曲里取该 mid 对应的歌手展示名(取不到退化为第一首的 Artist)。</summary>
    private static string ResolveSingerName(List<Song> songs, string singerMid)
    {
        foreach (var s in songs)
            for (var i = 0; i < s.ArtistMids.Count && i < s.ArtistNames.Count; i++)
                if (s.ArtistMids[i] == singerMid && s.ArtistNames[i].Length > 0)
                    return s.ArtistNames[i];
        return songs.FirstOrDefault()?.Artist ?? "";
    }
}
