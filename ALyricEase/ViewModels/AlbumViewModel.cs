using System.Collections.ObjectModel;
using System.Threading.Tasks;
using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services.NetEase;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ALyricEase.ViewModels;

/// <summary>专辑页:封面 + 名字 + 歌手 + 全量曲目。从歌单行/歌手页的专辑入口进入。</summary>
public sealed partial class AlbumViewModel : ViewModelBase
{
    private readonly NetEaseApiClient _api;
    private readonly PlayerViewModel _player;

    public AlbumViewModel(NetEaseApiClient api, PlayerViewModel player)
    {
        _api = api;
        _player = player;
    }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _artistName = "";
    [ObservableProperty] private string _trackCountText = "";
    [ObservableProperty] private long _publishTimeMs;
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private IImage? _cover;

    public bool HasDescription => !string.IsNullOrEmpty(Description);

    /// <summary>发行日期(如 2026-08-13);未知为空。</summary>
    public string PublishDateText => PublishTimeMs > 0
        ? DateTimeOffset.FromUnixTimeMilliseconds(PublishTimeMs).ToLocalTime().ToString("yyyy-MM-dd")
        : "";

    public ObservableCollection<SongItemViewModel> Songs { get; } = new();

    /// <summary>进入专辑页时调用:拉专辑信息 + 全量曲目。失败静默(保留旧内容)。</summary>
    public async Task LoadAsync(long albumId)
    {
        try
        {
            var album = await _api.GetAlbumAsync(albumId);
            Name = album.Info.Name;
            ArtistName = album.Info.Artists?.FirstOrDefault()?.Name ?? "";
            TrackCountText = $"{album.Songs.Count} 首";
            PublishTimeMs = album.Info.PublishTime;
            Description = album.Info.Description ?? "";
            if (album.Info.PicUrl is { Length: > 0 } pic) Cover = await CoverLoader.LoadAsync(pic, 300);

            var queue = album.Songs;
            Songs.Clear();
            var i = 1;
            foreach (var s in album.Songs)
                Songs.Add(new SongItemViewModel(s, _player.PlayFromList, i++, queue, _api));
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
