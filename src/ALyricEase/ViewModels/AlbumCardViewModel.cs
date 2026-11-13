using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.Services.NetEase;
using ALyricEase.Services.QQMusic;
using CommunityToolkit.Mvvm.Input;

namespace ALyricEase.ViewModels;

/// <summary>歌手页专辑/单曲卡片(200 宽):封面 + 标题 + 发布信息(原版歌手页同款"yyyy-M-d发布,共N首歌"),
/// 点击跳专辑页。封面后台加载。网易云按数字 id、QQ 按 mid 跳转。
/// 卡片悬停浮现的播放/更多操作要曲目,按需拉一次(<see cref="LoadSongsAsync"/>,成功即缓存)。</summary>
public sealed partial class AlbumCardViewModel : ViewModelBase
{
    private readonly string _mid;

    private List<Song>? _songs;

    private Task<IReadOnlyList<Song>>? _loadTask;

    public AlbumCardViewModel(long id, string title, string coverUrl, string mid = "",
        long publishTimeMs = 0, int songCount = 0)
    {
        Id = id;
        Title = title;
        CoverUrl = coverUrl;
        _mid = mid;
        PublishTimeMs = publishTimeMs;
        SongCount = songCount;
    }

    public long Id { get; }

    public string Title { get; }

    public string CoverUrl { get; }

    /// <summary>发行时间毫秒时间戳(0 = 未知,信息行省略日期段)。</summary>
    public long PublishTimeMs { get; }

    /// <summary>曲目数(0 = 未知,信息行省略歌曲数段)。网易云列表自带;QQ 由批量补拉回填。</summary>
    public int SongCount { get; private set; }

    /// <summary>QQ 曲数是列表加载后批量补拉的:回包后就地更新卡片信息行。</summary>
    internal void UpdateSongCount(int count)
    {
        if (count <= 0 || SongCount == count) return;
        SongCount = count;
        OnPropertyChanged(nameof(SongCount));
        OnPropertyChanged(nameof(SongCountText));
        OnPropertyChanged(nameof(InfoText));
        OnPropertyChanged(nameof(HasInfo));
    }

    /// <summary>卡片信息行:"2025-1-29发布，共18首歌"(原版歌手页同款);日期/曲数缺失的段省略,全缺为空串。</summary>
    public string InfoText
    {
        get
        {
            var date = PublishTimeMs > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(PublishTimeMs).LocalDateTime.ToString("yyyy-M-d") + "发布"
                : "";
            var count = SongCount > 0 ? $"共{SongCount}首歌" : "";
            return (date.Length, count.Length) switch
            {
                (0, 0) => "",
                (_, 0) => date,
                (0, _) => count,
                _ => $"{date}，{count}",
            };
        }
    }

    /// <summary>信息行显隐(有任何一段才显示,避免空 TextBlock 占行高)。</summary>
    public bool HasInfo => PublishTimeMs > 0 || SongCount > 0;

    /// <summary>详细列表的独立信息列，缺失值用占位符表示。</summary>
    public string PublishDateText => PublishTimeMs > 0
        ? DateTimeOffset.FromUnixTimeMilliseconds(PublishTimeMs).LocalDateTime.ToString("yyyy-M-d") + "发布"
        : "—";

    public string SongCountText => SongCount > 0 ? $"{SongCount} 首歌" : "—";

    /// <summary>QQ 专辑(mid 非空):跳 QQ 专辑页。</summary>
    public bool IsQq => _mid.Length > 0;

    internal string Mid => _mid;

    [RelayCommand]
    private async Task OpenAsync()
    {
        try
        {
            if (IsQq) await ServiceLocator.Get<MainViewModel>().OpenQqAlbumCommand.ExecuteAsync(_mid);
            else if (Id != 0) await ServiceLocator.Get<MainViewModel>().OpenAlbumCommand.ExecuteAsync(Id);
        }
        catch { /* 未初始化:忽略 */ }
    }

    /// <summary>卡片悬停 ▶ / ••• 菜单要用整张专辑的曲目。成功缓存,失败返回空(不抛给 UI,
    /// 下次点击还能重试);并发调用共享同一个在途任务。</summary>
    public Task<IReadOnlyList<Song>> LoadSongsAsync(CancellationToken ct = default)
    {
        if (_songs is { Count: > 0 }) return Task.FromResult<IReadOnlyList<Song>>(_songs);
        return _loadTask ??= LoadSongsCoreAsync(ct);
    }

    private async Task<IReadOnlyList<Song>> LoadSongsCoreAsync(CancellationToken ct)
    {
        try
        {
            List<Song> songs;
            if (IsQq)
            {
                songs = await ServiceLocator.Get<QQMusicApiClient>()
                    .GetAlbumSongsByMidAsync(_mid, 200, ct).ConfigureAwait(true);
            }
            else
            {
                var detail = await ServiceLocator.Get<NetEaseApiClient>()
                    .GetAlbumAsync(Id, ct).ConfigureAwait(true);
                songs = detail.Songs.ToList();
            }
            if (songs.Count > 0) _songs = songs;
            return songs;
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or TaskCanceledException)
        {
            return [];
        }
        finally
        {
            _loadTask = null; // 失败后允许重试;成功时 _songs 已缓存,不会再发请求
        }
    }

    /// <summary>播放整张专辑(第一首起播,队列 = 全量曲目)。</summary>
    [RelayCommand]
    public async Task PlayAsync()
    {
        var songs = await LoadSongsAsync().ConfigureAwait(true);
        if (songs.Count == 0) return;
        try
        {
            await ServiceLocator.Get<PlayerViewModel>()
                .PlayFromList(songs[0], songs, Title).ConfigureAwait(true);
        }
        catch { /* 播放失败由播放器提示 */ }
    }
}
