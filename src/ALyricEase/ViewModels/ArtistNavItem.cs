using System.Threading.Tasks;
using ALyricEase.Infrastructure;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ALyricEase.ViewModels;

/// <summary>多歌手子菜单项:网易云歌手 id 或 QQ singer mid + 名字,自带跳该歌手页命令。
/// 命令放在本项上,避免子菜单在 Popup 里跨逻辑树用 $parent 绑定取父级命令(会失效)。</summary>
public sealed partial class ArtistNavItem : ObservableObject
{
    private readonly string _mid;

    public ArtistNavItem(long id, string name, string mid = "")
    {
        Id = id;
        Name = name;
        _mid = mid;
    }

    public long Id { get; }

    public string Name { get; }

    /// <summary>QQ 项(mid 非空):跳 QQ 歌手页。</summary>
    public bool IsQq => _mid.Length > 0;

    /// <summary>有可跳转目标:网易云 id=0 的占位项(云盘无版权歌,服务端抹掉 id)不可跳,菜单里禁用。</summary>
    public bool HasTarget => IsQq || Id != 0;

    internal string Mid => _mid;

    [RelayCommand]
    private async Task OpenAsync()
    {
        try
        {
            if (IsQq) await ServiceLocator.Get<MainViewModel>().OpenQqArtistCommand.ExecuteAsync(_mid);
            else if (Id != 0) await ServiceLocator.Get<MainViewModel>().OpenArtistCommand.ExecuteAsync(Id);
        }
        catch { /* 未初始化:忽略 */ }
    }
}
