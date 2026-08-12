using System.Threading.Tasks;
using ALyricEase.Infrastructure;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ALyricEase.ViewModels;

/// <summary>歌手页专辑/单曲卡片(200 宽):封面 + 标题,点击跳专辑页。封面后台加载。</summary>
public sealed partial class AlbumCardViewModel : ViewModelBase
{
    private readonly string _coverUrl;
    private bool _coverRequested;

    public AlbumCardViewModel(long id, string title, string coverUrl)
    {
        Id = id;
        Title = title;
        _coverUrl = coverUrl;
    }

    public long Id { get; }

    public string Title { get; }

    /// <summary>容器 realized 时调用:首次才拉封面(幂等)。</summary>
    public void EnsureCoverLoaded()
    {
        if (_coverRequested || Cover is not null) return;
        _coverRequested = true;
        if (string.IsNullOrEmpty(_coverUrl)) return;
        _ = LoadCoverAsync();
    }

    [ObservableProperty] private IImage? _cover;

    [RelayCommand]
    private async Task OpenAsync()
    {
        if (Id == 0) return;
        try { await ServiceLocator.Get<MainViewModel>().OpenAlbumCommand.ExecuteAsync(Id); }
        catch { /* 未初始化:忽略 */ }
    }

    private async Task LoadCoverAsync() => Cover = await CoverLoader.LoadAsync(_coverUrl, 300);
}
