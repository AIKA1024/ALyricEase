using System.Threading.Tasks;
using ALyricEase.ViewModels;
using ALyricEase.Views;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace ALyricEase.Controls;

/// <summary>专辑卡(歌手页横滑 / 全部专辑页网格共用):封面上的 ▶ 播整张、••• 与右键/长按弹同一份
/// <see cref="AlbumCardContextMenu"/>。曲目按需拉(卡片 VM 缓存),拉不到就不弹菜单。</summary>
public partial class AlbumCard : UserControl
{
    public AlbumCard() => InitializeComponent();

    private async void OnPlayClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (DataContext is AlbumCardViewModel card)
            await card.PlayAsync();
    }

    private async void OnMoreClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button button && DataContext is AlbumCardViewModel card)
            await ShowMenuAsync(button, card, fromContextRequest: false);
    }

    /// <summary>桌面右键 / 触摸长按:与 ••• 同一份菜单,贴指针弹出。</summary>
    private async void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Button button || DataContext is not AlbumCardViewModel card) return;
        e.Handled = true;
        await ShowMenuAsync(button, card, fromContextRequest: true);
    }

    private static async Task ShowMenuAsync(
        Button anchor, AlbumCardViewModel card, bool fromContextRequest)
    {
        var songs = await card.LoadSongsAsync();
        if (songs.Count == 0) return; // 曲目拉不到:没有可播/可跳的内容,不弹空菜单
        AlbumCardContextMenu.Create(anchor, card.Title, songs, card.PlayAsync)
            .ShowAt(anchor, fromContextRequest);
    }
}
