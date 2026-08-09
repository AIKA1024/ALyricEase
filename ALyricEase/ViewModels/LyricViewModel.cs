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
