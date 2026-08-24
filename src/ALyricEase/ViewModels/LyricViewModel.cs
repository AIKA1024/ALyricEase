using System.Collections.ObjectModel;
using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services.Lrc;
using ALyricEase.Services.NetEase;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ALyricEase.ViewModels;

/// <summary>歌词 VM:加载解析 LRC(原文+翻译),按播放进度二分定位当前句。
/// 加载是异步的,集合更新统一回 UI 线程;切歌用版本号丢弃过期结果。</summary>
public sealed partial class LyricViewModel : ViewModelBase
{
    private readonly NetEaseApiClient _api;
    private readonly DispatcherService _dispatcher;
    private LyricDocument? _doc;
    private int _loadVersion;

    public LyricViewModel(NetEaseApiClient api, DispatcherService dispatcher)
    {
        _api = api;
        _dispatcher = dispatcher;
    }

    public ObservableCollection<LyricLine> Lines { get; } = new();

    [ObservableProperty] private int _currentIndex = -1;
    [ObservableProperty] private bool _hasLyric;
    [ObservableProperty] private string _message = "暂无歌词(纯音乐?)";

    /// <summary>是否显示翻译行(详情页歌词面板右下 aあ 开关;默认开,对齐原版)。</summary>
    [ObservableProperty] private bool _showTranslation = true;

    /// <summary>字号档位选中索引(详情页字号弹出菜单:自动/60/80/100/120/150%)。</summary>
    [ObservableProperty] private int _fontScaleIndex;

    // 字号档位 → 缩放系数;"自动"按 100% 处理。
    private static readonly double[] s_fontScales = [1.0, 0.6, 0.8, 1.0, 1.2, 1.5];

    /// <summary>详情页歌词字号缩放系数(1 = 100%)。</summary>
    public double LyricFontScale => s_fontScales[Math.Clamp(FontScaleIndex, 0, s_fontScales.Length - 1)];

    partial void OnFontScaleIndexChanged(int value) => OnPropertyChanged(nameof(LyricFontScale));

    public bool ShowEmpty => !HasLyric;

    partial void OnHasLyricChanged(bool value) => OnPropertyChanged(nameof(ShowEmpty));

    /// <summary>切歌/首播时调用:拉取并解析歌词。不阻塞播放,失败静默显示空态。</summary>
    public async Task LoadAsync(long songId)
    {
        var version = ++_loadVersion;
        Reset();

        LyricDocument? doc = null;
        try
        {
            var lrc = await _api.GetLyricAsync(songId).ConfigureAwait(false);
            if (lrc is not null)
                doc = LrcParser.Parse(lrc.Original, lrc.Translation);
        }
        catch (Exception)
        {
            doc = null; // 歌词失败不阻塞播放
        }

        _dispatcher.Post(() =>
        {
            if (version != _loadVersion) return; // 已切歌,丢弃过期结果
            if (doc is null || doc.IsEmpty)
            {
                Reset();
                return;
            }
            _doc = doc;
            Lines.Clear();
            foreach (var line in doc.Lines) Lines.Add(line);
            HasLyric = true;
            CurrentIndex = -1;
        });
    }

    /// <summary>播放进度回调(UI 线程):二分定位当前句。</summary>
    public void UpdatePosition(long positionMs)
    {
        if (_doc is null) return;
        var idx = _doc.FindIndex(positionMs);
        if (idx != CurrentIndex) CurrentIndex = idx;
    }

    private void Reset()
    {
        _doc = null;
        Lines.Clear();
        HasLyric = false;
        CurrentIndex = -1;
    }
}
