using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using ALyricEase.Infrastructure;
using ALyricEase.Services;
using ALyricEase.ViewModels;

namespace ALyricEase.Views;

/// <summary>私人FM页:绑定 PlayerViewModel(FM 无限流在播放器内实现)。
/// 进入页面时若 FM 未激活则自动拉起(幂等;未登录由 VM 提示)。</summary>
public partial class PersonalFmView : UserControl
{
    private MainViewModel? _main;
    private PlayerViewModel? _player;

    public PersonalFmView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdatePageLayout();
        DataContextChanged += (_, _) =>
        {
            if (this.IsAttachedToVisualTree()) ObservePlayer();
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ObservePlayer();
        _main = ServiceLocator.Provider?.GetService(typeof(MainViewModel)) as MainViewModel;
        if (_main is not null)
            _main.Playlist.PropertyChanged += OnLoginChanged;
        UpdateStartButton();
        if (DataContext is PlayerViewModel vm && vm.StartPersonalFmCommand.CanExecute(null))
            _ = vm.StartPersonalFmCommand.ExecuteAsync(null);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_main is not null)
            _main.Playlist.PropertyChanged -= OnLoginChanged;
        _main = null;
        if (_player is not null)
        {
            _player.PropertyChanged -= OnPlayerChanged;
            _player.PlayNextCommand.PropertyChanged -= OnNextCommandChanged;
        }
        _player = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void ObservePlayer()
    {
        if (_player is not null)
        {
            _player.PropertyChanged -= OnPlayerChanged;
            _player.PlayNextCommand.PropertyChanged -= OnNextCommandChanged;
        }
        _player = DataContext as PlayerViewModel;
        if (_player is not null)
        {
            _player.PropertyChanged += OnPlayerChanged;
            _player.PlayNextCommand.PropertyChanged += OnNextCommandChanged;
        }
        UpdatePageLayout();
        UpdateDiscoveryFeedback();
    }

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerViewModel.IsFmActive)) UpdatePageLayout();
        if (e.PropertyName is nameof(PlayerViewModel.IsLoading) or nameof(PlayerViewModel.IsFmActive))
            UpdateDiscoveryFeedback();
    }

    private void OnNextCommandChanged(object? sender, PropertyChangedEventArgs e)
        => UpdateDiscoveryFeedback();

    private void UpdateDiscoveryFeedback()
    {
        var loading = _player is { IsFmActive: true } && (_player.IsLoading || _player.PlayNextCommand.IsRunning);
        Classes.Set("loading", loading);
        RecommendationLabel.Text = loading ? "正在寻找下一首…" : "为你而来的一首";
    }

    private void OnLoginChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PlaylistViewModel.IsLoggedIn)) return;
        UpdateStartButton();
        if (_main?.Playlist.IsLoggedIn == true && DataContext is PlayerViewModel vm
            && vm.StartPersonalFmCommand.CanExecute(null))
            _ = vm.StartPersonalFmCommand.ExecuteAsync(null);
    }

    private void UpdateStartButton()
        => StartButton.Content = _main?.Playlist.IsLoggedIn == false ? "登录后收听" : "开始收听";

    private async void OnStartClicked(object? sender, RoutedEventArgs e)
    {
        if (_main?.Playlist.IsLoggedIn == false)
        {
            _main.OpenLoginDialogFor(MusicSource.NetEase);
            return;
        }
        if (DataContext is PlayerViewModel vm && vm.StartPersonalFmCommand.CanExecute(null))
            await vm.StartPersonalFmCommand.ExecuteAsync(null);
    }

    private void OnCoverClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PlayerViewModel { IsFmActive: true, CurrentSong: not null })
            _main?.OpenNowPlayingCommand.Execute(null);
    }

    private void UpdatePageLayout()
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;
        // 以页面可用宽度为断点；小屏直接堆叠封面和操作，不保留歌词占位。
        var stacked = Bounds.Width < 640;
        var inset = stacked ? 24 : 40;
        var width = Math.Min(800, Math.Max(0, Bounds.Width - inset * 2));
        PageLayout.Margin = new Thickness(inset, stacked ? 24 : 28);
        var minimumHeight = DataContext is PlayerViewModel { IsFmActive: true }
            ? (stacked ? 480 : 400) : 360;
        PageLayout.Height = Math.Max(minimumHeight, Bounds.Height - (stacked ? 48 : 56));
        Classes.Set("stacked", stacked);

        if (ListeningBody.Classes.Contains("stacked") != stacked || ListeningBody.RowDefinitions.Count == 0)
        {
            ListeningBody.Classes.Set("stacked", stacked);
            ListeningBody.ColumnDefinitions = new ColumnDefinitions(stacked ? "*" : "Auto,48,*");
            ListeningBody.RowDefinitions = new RowDefinitions(stacked ? "Auto,24,Auto" : "Auto");
            Grid.SetColumn(SongPanel, stacked ? 0 : 2);
            Grid.SetRow(SongPanel, stacked ? 2 : 0);
        }

        var coverSize = stacked
            ? Math.Min(width, Math.Clamp(Bounds.Height - 360, 160, 240))
            : Math.Min(340, Math.Min((width - 48) * 0.46, PageLayout.Height - 160));
        CoverButton.Width = CoverButton.Height = Math.Max(120, coverSize);
        CoverButton.HorizontalAlignment = stacked ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        var alignment = stacked ? TextAlignment.Center : TextAlignment.Left;
        SongTitle.TextAlignment = SongArtist.TextAlignment = SongAlbum.TextAlignment = alignment;
        RecommendationLabel.TextAlignment = DiscoveryHint.TextAlignment = alignment;
        SongTitle.FontSize = stacked ? 24 : 28;
        SongPanel.Spacing = stacked ? 16 : 24;
        Actions.HorizontalAlignment = stacked ? HorizontalAlignment.Center : HorizontalAlignment.Left;
    }
}
