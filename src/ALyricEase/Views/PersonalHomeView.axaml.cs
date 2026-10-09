using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ALyricEase.Infrastructure;
using ALyricEase.Services;
using ALyricEase.ViewModels;
using Visual = Avalonia.Visual;

namespace ALyricEase.Views;

public partial class PersonalHomeView : UserControl
{
    public PersonalHomeView()
    {
        InitializeComponent();
        _ = new DetailPageScrollController(this, PageScroller);
    }

    private static MainViewModel Main => ServiceLocator.Get<MainViewModel>();

    private void OnLoginClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AccountPlatformViewModel account })
            Main.OpenLoginDialogFor(account == Main.Account.Qq ? MusicSource.QQ : MusicSource.NetEase);
    }

    private void OnProfileClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: AccountPlatformViewModel account }) return;
        if (account == Main.Account.Qq) Main.OpenQqUserCommand.Execute(null);
        else if (long.TryParse(account.UserIdText, out var uid)) Main.OpenUserCommand.Execute(uid);
    }

    private void OnPlaylistTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Button { DataContext: PlaylistItemViewModel item } button) return;
        // 排除行/卡内操作按钮(▶/•••)的命中:Tapped 独立于 Click 冒泡,点击内部按钮时
        // 源的最近 Button 祖先不是行/卡自身(CollectedPlaylistsView 同款守卫)。
        if (e.Source is Visual source
            && source.FindAncestorOfType<Button>() is { } hit
            && !ReferenceEquals(hit, button)) return;
        Main.OpenShellPlaylistAuto(item);
    }

    private void OnPlaylistKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space) || sender is not Button { DataContext: PlaylistItemViewModel item }) return;
        e.Handled = true;
        Main.OpenShellPlaylistAuto(item);
    }

    private void OnPlayClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { DataContext: PlaylistItemViewModel item }) Main.Player.PlayPlaylistCommand.Execute(item);
    }

    private void OnMoreClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { DataContext: PlaylistItemViewModel item } button)
            PlaylistContextMenu.Create(button, item).ShowAt(button);
    }

    private void OnPlaylistContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Button { DataContext: PlaylistItemViewModel item } button) return;
        e.Handled = true;
        PlaylistContextMenu.Create(button, item).ShowAt(button, true);
    }
}
