using System.Collections.ObjectModel;
using System.Threading.Tasks;
using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services.NetEase;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ALyricEase.ViewModels;

/// <summary>歌手页:头部(圆形头像 + 名字)+ 热门歌曲横向网格 + 专辑/单曲卡片网格。从歌单行/专辑行歌手入口进入。</summary>
public sealed partial class ArtistViewModel : ViewModelBase
{
    private readonly NetEaseApiClient _api;
    private readonly PlayerViewModel _player;

    public ArtistViewModel(NetEaseApiClient api, PlayerViewModel player)
    {
        _api = api;
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

    /// <summary>进入歌手页时调用:拉歌手信息 + 热门歌曲 + 专辑/单曲。失败静默(保留旧内容)。</summary>
    public async Task LoadAsync(long artistId)
    {
        try
        {
            var info = await _api.GetArtistAsync(artistId);
            Name = info.Name;
            if (info.Avatar is { Length: > 0 } pic) Avatar = await CoverLoader.LoadAsync(pic, 240);

            var songs = await _api.GetArtistSongsAsync(artistId, 30);
            Subtitle = $"{songs.Count} 首单曲";
            var queue = songs;
            Songs.Clear();
            foreach (var s in songs)
                Songs.Add(new SongItemViewModel(s, _player.PlayFromList, api: _api, queue: queue));

            var albums = await _api.GetArtistAlbumsAsync(artistId, 50);
            Albums.Clear();
            Singles.Clear();
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
            // 网络失败静默:保留旧内容,不崩
        }
    }

    [RelayCommand]
    private async Task PlayAllAsync()
    {
        if (Songs.Count == 0) return;
        await Songs[0].PlayCommand.ExecuteAsync(null);
    }
}
