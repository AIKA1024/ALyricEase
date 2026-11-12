using System.Collections.Generic;
using System.ComponentModel;
using ALyricEase.Services;
using Avalonia.Media;
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
        _fontFamilyName = state.DesktopLyricFontFamily;
        _textColor = ParseColor(state.DesktopLyricTextColor, DefaultTextColor);
        _outlineEnabled = state.DesktopLyricOutlineEnabled;
        _outlineThickness = state.DesktopLyricOutlineThickness;
        _outlineColor = ParseColor(state.DesktopLyricOutlineColor, Colors.Black);
        _shadowEnabled = state.DesktopLyricShadowEnabled;
        _shadowBlur = state.DesktopLyricShadowBlur;
        _shadowColor = ParseColor(state.DesktopLyricShadowColor, Colors.Black);
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
    // 字号/背景/穿透的增减按钮随顶栏一起去掉了:改值走设置弹窗,开关走 HotkeyService 的三个动作。

    // ---- 文本外观:字体家族 / 文字颜色 / 描边 / 投影 ----
    // 全部即时生效:歌词窗口与弹窗预览绑的是同一份属性,改一个值两处一起动。

    private static readonly Color DefaultTextColor = Color.Parse("#FF72E5D3");
    private const double DefaultFontSize = 28;
    private const double DefaultBackgroundOpacity = 0.85;
    private const double DefaultOutlineThickness = 2;
    private const double DefaultShadowBlur = 3;

    /// <summary>字体家族名;null / 空 = 跟随应用默认字体(Inter)。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LyricFontFamily))]
    [NotifyPropertyChangedFor(nameof(FontFamilyIndex))]
    private string? _fontFamilyName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TextBrush))]
    private Color _textColor;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOutline))]
    [NotifyPropertyChangedFor(nameof(EffectiveOutlineThickness))]
    private bool _outlineEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOutline))]
    [NotifyPropertyChangedFor(nameof(EffectiveOutlineThickness))]
    [NotifyPropertyChangedFor(nameof(OutlineThicknessText))]
    private double _outlineThickness;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutlineBrush))]
    private Color _outlineColor;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LyricShadow))]
    [NotifyPropertyChangedFor(nameof(ShadowBlurText))]
    private bool _shadowEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LyricShadow))]
    [NotifyPropertyChangedFor(nameof(ShadowBlurText))]
    private double _shadowBlur;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LyricShadow))]
    private Color _shadowColor;

    public FontFamily? LyricFontFamily =>
        string.IsNullOrWhiteSpace(FontFamilyName) ? null : new FontFamily(FontFamilyName);

    public IBrush TextBrush => new SolidColorBrush(TextColor);
    public IBrush OutlineBrush => new SolidColorBrush(OutlineColor);
    /// <summary>翻译行固定近白色:主歌词换色后仍能一眼分出主次。</summary>
    public IBrush TranslationBrush => new SolidColorBrush(Color.FromArgb(230, 255, 255, 255));

    public bool ShowOutline => OutlineEnabled && OutlineThickness > 0;
    /// <summary>描边像素宽度;关掉时给 0,绘制端据此跳过展开的 8 份描边副本。</summary>
    public double EffectiveOutlineThickness => ShowOutline ? OutlineThickness : 0;
    public string OutlineThicknessText => OutlineThickness.ToString("0.0");
    public string ShadowBlurText => ShadowBlur.ToString("0");

    /// <summary>
    /// 投影:关掉返回 null(不挂 Effect 才是真的不画)。偏移固定 1px,强度只由模糊半径控制。
    /// </summary>
    public IEffect? LyricShadow => ShadowEnabled && ShadowBlur > 0
        ? new DropShadowEffect { Color = ShadowColor, BlurRadius = ShadowBlur, OffsetX = 0, OffsetY = 1 }
        : null;

    /// <summary>
    /// 描边靠"同一段文字画 8 层"实现,只有 Avalonia 那套视图能做;
    /// Android 悬浮窗是原生 TextView(没有文字描边) ⇒ 那一端不展示"文字描边"卡片。
    /// </summary>
    public bool SupportsOutline => !OperatingSystem.IsAndroid();

    /// <summary>系统已安装字体;第一项是"跟随系统默认"。首次访问时才枚举(启动时不付这份开销)。</summary>
    private IReadOnlyList<LyricFontOption>? _fontOptions;
    public IReadOnlyList<LyricFontOption> FontOptions => _fontOptions ??= BuildFontOptions();

    /// <summary>字体下拉的选中项;认不出来的名字(字体已卸载)落到第 0 项。</summary>
    public int FontFamilyIndex
    {
        get
        {
            if (string.IsNullOrWhiteSpace(FontFamilyName)) return 0;
            var options = FontOptions;
            for (var i = 1; i < options.Count; i++)
                if (string.Equals(options[i].FamilyName, FontFamilyName, StringComparison.OrdinalIgnoreCase))
                    return i;
            return 0;
        }
        set
        {
            var options = FontOptions;
            if (value < 0 || value >= options.Count) return;
            FontFamilyName = options[value].FamilyName;
        }
    }

    private static IReadOnlyList<LyricFontOption> BuildFontOptions()
    {
        var options = new List<LyricFontOption> { new(null, "跟随系统默认") };
        try
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var family in FontManager.Current.SystemFonts)
            {
                var name = family.Name;
                if (string.IsNullOrWhiteSpace(name) || !seen.Add(name)) continue;
                options.Add(new LyricFontOption(name, name));
            }
        }
        catch (Exception)
        {
            // 取不到系统字体列表(Headless / 受限平台)不算错误:只剩"跟随系统默认"一项。
        }
        // 系统字体在前,按名字排;默认项固定第一。
        var sorted = options.Skip(1).OrderBy(o => o.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        options.RemoveRange(1, options.Count - 1);
        options.AddRange(sorted);
        return options;
    }

    private static Color ParseColor(string? value, Color fallback) =>
        !string.IsNullOrWhiteSpace(value) && Color.TryParse(value, out var parsed) ? parsed : fallback;

    /// <summary>把颜色写回配置:存 #AARRGGBB,与 AppStateStore 的归一格式一致。</summary>
    private void SaveColor(Color color, Action<string> write)
    {
        write($"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}");
        _state.ScheduleSave();
    }

    partial void OnFontFamilyNameChanged(string? value)
    {
        _state.DesktopLyricFontFamily = string.IsNullOrWhiteSpace(value) ? null : value;
        _state.Save();
    }

    partial void OnTextColorChanged(Color value) => SaveColor(value, v => _state.DesktopLyricTextColor = v);
    partial void OnOutlineColorChanged(Color value) => SaveColor(value, v => _state.DesktopLyricOutlineColor = v);
    partial void OnShadowColorChanged(Color value) => SaveColor(value, v => _state.DesktopLyricShadowColor = v);

    partial void OnOutlineEnabledChanged(bool value)
    {
        _state.DesktopLyricOutlineEnabled = value;
        _state.Save();
    }

    partial void OnOutlineThicknessChanged(double value)
    {
        var normalized = double.IsFinite(value) ? Math.Clamp(value, 0.5, 4) : DefaultOutlineThickness;
        if (value != normalized) { OutlineThickness = normalized; return; }
        _state.DesktopLyricOutlineThickness = value;
        _state.ScheduleSave();
    }

    partial void OnShadowEnabledChanged(bool value)
    {
        _state.DesktopLyricShadowEnabled = value;
        _state.Save();
    }

    partial void OnShadowBlurChanged(double value)
    {
        var normalized = double.IsFinite(value) ? Math.Clamp(value, 0, 16) : DefaultShadowBlur;
        if (value != normalized) { ShadowBlur = normalized; return; }
        _state.DesktopLyricShadowBlur = value;
        _state.ScheduleSave();
    }

    /// <summary>恢复默认外观(不动行为开关:翻译、暂停隐藏、穿透、总开关都保留)。</summary>
    [RelayCommand]
    private void ResetAppearance()
    {
        FontSize = DefaultFontSize;
        BackgroundOpacity = DefaultBackgroundOpacity;
        FontFamilyName = null;
        TextColor = DefaultTextColor;
        OutlineEnabled = false;
        OutlineThickness = DefaultOutlineThickness;
        OutlineColor = Colors.Black;
        ShadowEnabled = true;
        ShadowBlur = DefaultShadowBlur;
        ShadowColor = Colors.Black;
    }

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

/// <summary>字体下拉的一项。<see cref="FamilyName"/> 为 null = 跟随应用默认字体。</summary>
public sealed class LyricFontOption
{
    public LyricFontOption(string? familyName, string displayName)
    {
        FamilyName = familyName;
        DisplayName = displayName;
    }

    public string? FamilyName { get; }
    public string DisplayName { get; }
}
