using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading.Tasks;
using ALyricEase.Models;
using ALyricEase.Services.NetEase;
using ALyricEase.Services.QQMusic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ALyricEase.ViewModels;

/// <summary>专辑页:封面 + 名字 + 歌手 + 全量曲目。从歌单行/歌手页的专辑入口进入。
/// 网易云按数字 id、QQ 按 album mid 取数(两套 Load)。</summary>
public sealed partial class AlbumViewModel : ViewModelBase
{
    private readonly NetEaseApiClient _api;
    private readonly QQMusicApiClient _qqApi;
    private readonly PlayerViewModel _player;

    public AlbumViewModel(NetEaseApiClient api, QQMusicApiClient qqApi, PlayerViewModel player)
    {
        _api = api;
        _qqApi = qqApi;
        _player = player;
    }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _artistName = "";
    [ObservableProperty] private string _trackCountText = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PublishDateText))]
    private long _publishTimeMs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDescription))]
    private string _description = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasArtist))]
    private ArtistNavItem? _primaryArtist;

    [ObservableProperty] private string _coverUrl = "";

    public bool HasDescription => !string.IsNullOrEmpty(Description);

    public bool HasArtist => PrimaryArtist is not null;

    /// <summary>发行日期(如 2026-08-13);未知为空。</summary>
    public string PublishDateText => PublishTimeMs > 0
        ? DateTimeOffset.FromUnixTimeMilliseconds(PublishTimeMs).ToLocalTime().ToString("yyyy-MM-dd")
        : "";

    public ObservableCollection<SongItemViewModel> Songs { get; } = new();

    /// <summary>进入专辑页时调用:先清掉上一位(上一张专辑)的内容,再拉专辑信息 + 全量曲目。
    /// 失败静默(页面停在空态,而不是上一张专辑的数据)。</summary>
    public async Task LoadAsync(long albumId)
    {
        ClearContent();
        try
        {
            var album = await _api.GetAlbumAsync(albumId);
            Name = album.Info.Name;
            var artists = album.Info.Artists?
                .Where(artist => !string.IsNullOrWhiteSpace(artist.Name))
                .ToList() ?? [];
            ArtistName = string.Join("/", artists.Select(artist => artist.Name));
            var primaryArtist = artists.FirstOrDefault(artist => artist.Id != 0);
            PrimaryArtist = primaryArtist is null
                ? null
                : new ArtistNavItem(primaryArtist.Id, primaryArtist.Name);
            TrackCountText = $"{album.Songs.Count} 首";
            PublishTimeMs = album.Info.PublishTime;
            Description = album.Info.Description ?? "";
            if (album.Info.PicUrl is { Length: > 0 } pic) CoverUrl = pic;

            var queue = album.Songs;
            var i = 1;
            foreach (var s in album.Songs)
                Songs.Add(new SongItemViewModel(s, _player.PlayFromList, i++, queue, _api, album.Info.Name));
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
        var firstPlayable = Songs.FirstOrDefault(song => song.IsPlayable);
        if (firstPlayable is not null)
            await firstPlayable.PlayCommand.ExecuteAsync(null);
    }

    /// <summary>点击专辑头部歌手名，按音源进入主歌手详情页。</summary>
    [RelayCommand]
    private async Task OpenArtistAsync()
    {
        if (PrimaryArtist is not null)
            await PrimaryArtist.OpenCommand.ExecuteAsync(null);
    }

    /// <summary>QQ 音乐专辑页(按 album mid):信息 + 曲目并行拉;发行日期为 "yyyy-MM-dd" 文本。
    /// 歌手名取自曲目(详情接口的 singer 结构不稳定)。失败静默(停在空态)。</summary>
    public async Task LoadQqAsync(string albumMid)
    {
        ClearContent();
        try
        {
            var infoTask = _qqApi.GetAlbumInfoByMidAsync(albumMid);
            var songsTask = _qqApi.GetAlbumSongsByMidAsync(albumMid);
            await Task.WhenAll(infoTask, songsTask).ConfigureAwait(true);
            var info = infoTask.Result;
            var songs = songsTask.Result;

            Name = info.Name;
            var leadSong = songs.FirstOrDefault();
            ArtistName = leadSong?.Artist ?? "";
            var primaryArtist = leadSong?.ArtistMids
                .Zip(leadSong.ArtistNames, (mid, name) => (Mid: mid, Name: name))
                .FirstOrDefault(artist => !string.IsNullOrWhiteSpace(artist.Mid));
            PrimaryArtist = string.IsNullOrWhiteSpace(primaryArtist?.Mid)
                ? null
                : new ArtistNavItem(0, primaryArtist.Value.Name, primaryArtist.Value.Mid);
            TrackCountText = $"{songs.Count} 首";
            PublishTimeMs = DateTimeOffset.TryParse(info.PublishDate, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var d) ? d.ToUnixTimeMilliseconds() : 0;
            Description = info.Description;
            CoverUrl = $"https://y.gtimg.cn/music/photo_new/T002R300x300M000{albumMid}.jpg";

            var i = 1;
            foreach (var s in songs)
                Songs.Add(new SongItemViewModel(s, _player.PlayFromList, i++, songs, source: info.Name));
        }
        catch
        {
            // 网络失败静默:停在空态,不崩
        }
    }

    /// <summary>清空上一张专辑的内容。专辑页每次进入都重建视图并立即绑定现有内容,
    /// 不清的话新页面会先渲染出上一张专辑的曲目(加载失败时更会整页停在错位数据上)。</summary>
    private void ClearContent()
    {
        Name = "";
        ArtistName = "";
        TrackCountText = "";
        PublishTimeMs = 0;
        Description = "";
        PrimaryArtist = null;
        CoverUrl = "";
        Songs.Clear();
    }
}
