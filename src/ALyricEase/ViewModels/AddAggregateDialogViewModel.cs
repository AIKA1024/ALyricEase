using System;
using System.Collections.ObjectModel;
using System.Linq;
using ALyricEase.Models;
using ALyricEase.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ALyricEase.ViewModels;

/// <summary>聚合歌单选择对话框 VM:列出网易云/QQ 音乐两源已加载的用户歌单供多选,
/// 可选命名(留空自动命名);确认后构造 AggregatePlaylist 交给宿主(MainViewModel)
/// 持久化并刷新侧栏聚合分组。</summary>
public sealed partial class AddAggregateDialogViewModel : ViewModelBase
{
    private readonly PlaylistViewModel _playlist;
    private readonly AppStateStore _appState;
    private readonly Action<AggregatePlaylist> _onConfirm;

    public AddAggregateDialogViewModel(PlaylistViewModel playlist, AppStateStore appState, Action<AggregatePlaylist> onConfirm)
    {
        _playlist = playlist;
        _appState = appState;
        _onConfirm = onConfirm;
    }

    public ObservableCollection<AggregatePickerItemViewModel> NetEaseItems { get; } = new();

    public ObservableCollection<AggregatePickerItemViewModel> QqItems { get; } = new();

    /// <summary>网易云分区是否有可选项(驱动分区显隐)。</summary>
    [ObservableProperty] private bool _hasNetEase;

    /// <summary>QQ音乐分区是否有可选项。</summary>
    [ObservableProperty] private bool _hasQq;

    /// <summary>聚合歌单名称(可选;留空确认时自动命名)。</summary>
    [ObservableProperty] private string _name = "";

    /// <summary>两源都没有可选项时显示提示。</summary>
    public bool HasNothing => !HasNetEase && !HasQq;

    /// <summary>至少勾选一个歌单才可确认。</summary>
    public bool CanConfirm => NetEaseItems.Concat(QqItems).Any(i => i.IsChecked);

    /// <summary>打开对话框时调用:用两源已加载的歌单重建候选列表。</summary>
    public void Refresh()
    {
        Name = "";
        NetEaseItems.Clear();
        QqItems.Clear();
        foreach (var p in _playlist.Playlists)
            AddItem(NetEaseItems, MusicSource.NetEase, p.Id, p.Name, p.TrackCount);
        foreach (var p in _playlist.QqPlaylists)
            AddItem(QqItems, MusicSource.QQ, p.Id, p.Name, p.TrackCount);
        HasNetEase = NetEaseItems.Count > 0;
        HasQq = QqItems.Count > 0;
        OnPropertyChanged(nameof(CanConfirm));
        OnPropertyChanged(nameof(HasNothing));
    }

    private void AddItem(ObservableCollection<AggregatePickerItemViewModel> list, MusicSource source, long id, string name, int trackCount)
    {
        var item = new AggregatePickerItemViewModel(source, id, name, trackCount);
        // 任一勾选变化 → 刷新确认按钮可用态
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AggregatePickerItemViewModel.IsChecked))
                OnPropertyChanged(nameof(CanConfirm));
        };
        list.Add(item);
    }

    [RelayCommand]
    private void Confirm()
    {
        var picked = NetEaseItems.Concat(QqItems).Where(i => i.IsChecked).ToList();
        if (picked.Count == 0) return;
        var aggregate = new AggregatePlaylist
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = Name.Trim().Length > 0 ? Name.Trim() : AutoName(),
            Members = picked.Select(i => new AggregatePlaylistMember
            {
                Source = i.Source,
                PlaylistId = i.PlaylistId,
                PlaylistName = i.Name,
            }).ToList(),
        };
        _onConfirm(aggregate);
    }

    /// <summary>未命名自动生成:"聚合歌单 N"(按现有个数顺延,尽力避免重名)。</summary>
    private string AutoName() => $"聚合歌单 {_appState.AggregatePlaylists.Count + 1}";
}

/// <summary>聚合歌单候选行:源 + 歌单 id + 展示名 + 曲目数 + 勾选状态。</summary>
public sealed partial class AggregatePickerItemViewModel : ViewModelBase
{
    public AggregatePickerItemViewModel(MusicSource source, long playlistId, string name, int trackCount)
    {
        Source = source;
        PlaylistId = playlistId;
        Name = name;
        TrackCountText = trackCount > 0 ? $"{trackCount} 首" : "";
        SourceLabel = source == MusicSource.QQ ? "QQ音乐" : "网易云";
    }

    public MusicSource Source { get; }

    public long PlaylistId { get; }

    public string Name { get; }

    /// <summary>来源显示名(分区内冗余,备用)。</summary>
    public string SourceLabel { get; }

    public string TrackCountText { get; }

    [ObservableProperty] private bool _isChecked;
}
