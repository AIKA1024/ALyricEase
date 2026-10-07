using System.Threading.Tasks;
using ALyricEase.Infrastructure;
using CommunityToolkit.Mvvm.Input;

namespace ALyricEase.ViewModels;

/// <summary>歌手页专辑/单曲卡片(200 宽):封面 + 标题 + 发布信息(原版歌手页同款"yyyy-M-d发布,共N首歌"),
/// 点击跳专辑页。封面后台加载。网易云按数字 id、QQ 按 mid 跳转。</summary>
public sealed partial class AlbumCardViewModel : ViewModelBase
{
    private readonly string _mid;

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
}
