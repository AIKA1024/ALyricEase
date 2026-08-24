using System.Threading.Tasks;
using ALyricEase.Infrastructure;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ALyricEase.ViewModels;

/// <summary>多歌手子菜单项:歌手 id + 名字,自带跳该歌手页命令。
/// 命令放在本项上,避免子菜单在 Popup 里跨逻辑树用 $parent 绑定取父级命令(会失效)。</summary>
public sealed partial class ArtistNavItem : ObservableObject
{
    public ArtistNavItem(long id, string name)
    {
        Id = id;
        Name = name;
    }

    public long Id { get; }

    public string Name { get; }

    [RelayCommand]
    private async Task OpenAsync()
    {
        if (Id == 0) return;
        try { await ServiceLocator.Get<MainViewModel>().OpenArtistCommand.ExecuteAsync(Id); }
        catch { /* 未初始化:忽略 */ }
    }
}
