using System.ComponentModel;
using ALyricEase.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ALyricEase.ViewModels;

/// <summary>两端悬浮歌词共用的状态；只观察现有歌词时间轴，不额外请求歌词或启动轮询。</summary>
public sealed partial class DesktopLyricViewModel : ViewModelBase, IDisposable
{
    private readonly AppStateStore _state;
    private readonly PlayerViewModel _player;
    private readonly LyricViewModel _lyric;

    public DesktopLyricViewModel(AppStateStore state, PlayerViewModel player, LyricViewModel lyric)
    {
        _state = state;
        _player = player;
        _lyric = lyric;
        _enabled = state.DesktopLyricEnabled;
        _clickThrough = state.DesktopLyricClickThrough;
        _showTranslation = state.DesktopLyricShowTranslation;
        _hideWhenPaused = state.DesktopLyricHideWhenPaused;
        _fontSize = state.DesktopLyricFontSize;
        _backgroundOpacity = state.DesktopLyricBackgroundOpacity;
        player.PropertyChanged += OnPlaybackChanged;
        lyric.PropertyChanged += OnLyricChanged;
        RefreshText();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(IsWindowVisible))]
    private bool _enabled;
    public string StatusText => Enabled ? "已开启" : "已关闭";
    [ObservableProperty] private bool _showTranslation;
    /// <summary>暂停 / 停止时收起歌词窗口。歌词开着的用户随时可以再播、或用快捷键切歌。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWindowVisible))]
    private bool _hideWhenPaused;
    /// <summary>
    /// 歌词窗口此刻该不该出现。<see cref="PlayerViewModel.IsLoading"/> 也算"在播"——
    /// 换歌时状态会短暂落到 Loading(IsPlaying=false),只看 IsPlaying 会让窗口每切一首闪一下。
    /// </summary>
    public bool IsWindowVisible => Enabled && (!HideWhenPaused || _player.IsPlaying || _player.IsLoading);
    [ObservableProperty] private double _fontSize;
    [ObservableProperty] private double _backgroundOpacity;
    // 穿透状态会随配置恢复:重启后歌词仍然不挡鼠标。解除方式有三种(设置里的开关、
    // 桌面歌词穿透快捷键、关闭歌词),所以不再是"恢复后就找不回来"的陷阱。
    [ObservableProperty] private bool _clickThrough;
    public Avalonia.Media.IBrush BackgroundBrush => new Avalonia.Media.SolidColorBrush(
        Avalonia.Media.Color.FromArgb((byte)Math.Round(255 * BackgroundOpacity), 35, 39, 45));
    partial void OnBackgroundOpacityChanged(double value)
    {
        var normalized = double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0.85;
        if (value != normalized) { BackgroundOpacity = normalized; return; }
        _state.DesktopLyricBackgroundOpacity = value;
        _state.ScheduleSave();
        OnPropertyChanged(nameof(BackgroundBrush));
    }
    [RelayCommand] private void IncreaseFont() => FontSize = Math.Min(48, FontSize + 2);
    [RelayCommand] private void DecreaseFont() => FontSize = Math.Max(16, FontSize - 2);
    [RelayCommand] private void ToggleBackground() => BackgroundOpacity = BackgroundOpacity > 0 ? 0 : 0.85;
    [RelayCommand] private void EnableClickThrough() => ClickThrough = true;
    [ObservableProperty] private string _original = "ALyricEase";
    [ObservableProperty] private string _translation = "";
    [ObservableProperty] private string _hint = "在其他应用上方显示歌词，可拖动调整位置。";
    public double TranslationFontSize => FontSize * 0.7;
    public bool HasTranslation => !string.IsNullOrWhiteSpace(Translation);

    partial void OnEnabledChanged(bool value)
    {
        _state.DesktopLyricEnabled = value;
        // 关掉歌词就没有窗口可穿透:顺手收回,别让下次打开一上来就点不动。
        if (!value) ClickThrough = false;
        _state.Save();
    }

    partial void OnClickThroughChanged(bool value)
    {
        _state.DesktopLyricClickThrough = value;
        _state.Save();
    }

    partial void OnShowTranslationChanged(bool value)
    {
        _state.DesktopLyricShowTranslation = value;
        _state.Save();
        RefreshText();
    }

    partial void OnHideWhenPausedChanged(bool value)
    {
        _state.DesktopLyricHideWhenPaused = value;
        _state.Save();
    }

    partial void OnFontSizeChanged(double value)
    {
        var normalized = double.IsFinite(value) ? Math.Clamp(value, 16, 48) : 28;
        if (value != normalized) { FontSize = normalized; return; }
        _state.DesktopLyricFontSize = value;
        _state.ScheduleSave();
        OnPropertyChanged(nameof(TranslationFontSize));
    }

    partial void OnTranslationChanged(string value) => OnPropertyChanged(nameof(HasTranslation));

    private void OnPlaybackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerViewModel.CurrentSong)) RefreshText();
        // 暂停/继续、换歌加载都会改变窗口该不该出现。
        if (e.PropertyName is nameof(PlayerViewModel.IsPlaying) or nameof(PlayerViewModel.IsLoading))
            OnPropertyChanged(nameof(IsWindowVisible));
    }

    private void OnLyricChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LyricViewModel.CurrentIndex) or nameof(LyricViewModel.Lines)
            or nameof(LyricViewModel.HasLyric) or nameof(LyricViewModel.Song)) RefreshText();
    }

    private void RefreshText()
    {
        var song = _player.CurrentSong;
        var index = _lyric.CurrentIndex;
        var line = song is not null && ReferenceEquals(song, _lyric.Song)
            && index >= 0 && index < _lyric.Lines.Count ? _lyric.Lines[index] : null;
        Original = line is not null && !string.IsNullOrWhiteSpace(line.Original)
            ? line.Original : song?.Name ?? "ALyricEase";
        Translation = line is null
            ? (song is null ? "播放音乐后显示歌词" : song.Artist)
            : ShowTranslation ? line.Translation ?? "" : "";
    }

    [RelayCommand] private void Close() => Enabled = false;

    public void Dispose()
    {
        _player.PropertyChanged -= OnPlaybackChanged;
        _lyric.PropertyChanged -= OnLyricChanged;
    }
}
