using System.Threading.Tasks;
using ALyricEase.Infrastructure;
using CommunityToolkit.Mvvm.Input;

namespace ALyricEase.ViewModels;

/// <summary>歌手页专辑/单曲卡片(200 宽):封面 + 标题,点击跳专辑页。封面后台加载。网易云按数字 id、QQ 按 mid 跳转。</summary>
public sealed partial class AlbumCardViewModel : ViewModelBase
{
    private readonly string _mid;

    public AlbumCardViewModel(long id, string title, string coverUrl, string mid = "")
    {
        Id = id;
        Title = title;
        CoverUrl = coverUrl;
        _mid = mid;
    }

    public long Id { get; }

    public string Title { get; }

    public string CoverUrl { get; }

    /// <summary>QQ 专辑(mid 非空):跳 QQ 专辑页。</summary>
    public bool IsQq => _mid.Length > 0;

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
