using System;
using System.Collections.ObjectModel;
using System.Linq;
using ALyricEase.Models;
using ALyricEase.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ALyricEase.ViewModels;

/// <summary>聚合歌单选择对话框 VM:列出网易云/QQ 音乐两源已加载的用户歌单供多选。
/// 双模式:添加(可选命名,留空自动命名;确认构造新 AggregatePlaylist 交宿主持久化)与
/// 编辑(侧栏聚合子项右键"选择成员歌单"打开,预勾现有成员;某源未登录/未加载时,
/// 该源现有成员按引用兜底补进候选,避免确认时被静默丢掉;确认后交宿主原位更新)。</summary>
public sealed partial class AddAggregateDialogViewModel : ViewModelBase
{
    private readonly PlaylistViewModel _playlist;
    private readonly AppStateStore _appState;
    private readonly Action<AggregatePlaylist> _onConfirm;
    private readonly Action<AggregatePlaylist> _onEditConfirm;

    /// <summary>编辑模式目标(null = 添加模式)。</summary>
    private AggregatePlaylist? _editing;

    public AddAggregateDialogViewModel(PlaylistViewModel playlist, AppStateStore appState,
        Action<AggregatePlaylist> onConfirm, Action<AggregatePlaylist> onEditConfirm)
    {
        _playlist = playlist;
        _appState = appState;
        _onConfirm = onConfirm;
        _onEditConfirm = onEditConfirm;
    }

    public ObservableCollection<AggregatePickerItemViewModel> NetEaseItems { get; } = new();

    public ObservableCollection<AggregatePickerItemViewModel> QqItems { get; } = new();

    public ObservableCollection<AggregatePickerItemViewModel> LocalItems { get; } = new();

    /// <summary>网易云分区是否有可选项(驱动分区显隐)。</summary>
    [ObservableProperty] private bool _hasNetEase;

    /// <summary>QQ音乐分区是否有可选项。</summary>
    [ObservableProperty] private bool _hasQq;

    /// <summary>本地音乐分区是否有可选项。</summary>
    [ObservableProperty] private bool _hasLocal;

    /// <summary>聚合歌单名称(添加模式可选留空自动命名;编辑模式预填现名,留空保留现名)。</summary>
    [ObservableProperty] private string _name = "";

    /// <summary>所有分区都没有可选项时显示提示。</summary>
    public bool HasNothing => !HasNetEase && !HasQq && !HasLocal;

    /// <summary>至少勾选一个歌单才可确认。</summary>
    public bool CanConfirm => NetEaseItems.Concat(QqItems).Concat(LocalItems).Any(i => i.IsChecked);

    /// <summary>标题(添加/编辑模式区分)。</summary>
    public string TitleText => _editing is null ? "添加聚合歌单" : "选择成员歌单";

    /// <summary>确认按钮文案。</summary>
    public string ConfirmText => _editing is null ? "添加" : "保存";

    /// <summary>名称输入框占位文案。</summary>
    public string NamePlaceholder => _editing is null
        ? "聚合歌单名称(可选,留空自动命名)"
        : "聚合歌单名称";

    /// <summary>打开对话框时调用(添加模式):用两源已加载的歌单重建候选列表。</summary>
    public void Refresh() => RefreshCore(null);

    /// <summary>打开对话框时调用(编辑模式):预勾现有成员;成员所在源未加载时按成员引用兜底补行。</summary>
    public void Refresh(AggregatePlaylist existing) => RefreshCore(existing);

    private void RefreshCore(AggregatePlaylist? editing)
    {
        _editing = editing;
        Name = editing?.Name ?? "";
        NetEaseItems.Clear();
        QqItems.Clear();
        LocalItems.Clear();
        foreach (var p in _playlist.Playlists)
            AddItem(NetEaseItems, MusicSource.NetEase, p.Id, p.Name, p.TrackCount);
        foreach (var p in _playlist.QqPlaylists)
            AddItem(QqItems, MusicSource.QQ, p.Id, p.Name, p.TrackCount);
        foreach (var lp in _appState.LocalPlaylists)
            AddLocalItem(lp.Id, lp.Name, lp.Tracks.Count);

        if (editing is not null)
        {
            // 预勾现有成员;候选里没有的(成员所在源未登录/未加载)按成员引用兜底补行,
            // 保证现有成员始终可见、可主动取消 —— 否则确认会把它们静默丢掉
            foreach (var m in editing.Members)
            {
                if (m.Source == MusicSource.Local)
                {
                    var localItem = LocalItems.FirstOrDefault(i => i.LocalPlaylistId == m.LocalPlaylistId)
                        ?? AddLocalItem(m.LocalPlaylistId ?? "", m.PlaylistName, 0);
                    localItem.IsChecked = true;
                    continue;
                }
                var items = m.Source == MusicSource.QQ ? QqItems : NetEaseItems;
                var existingItem = items.FirstOrDefault(i => i.PlaylistId == m.PlaylistId);
                if (existingItem is null)
                    existingItem = AddItem(items, m.Source, m.PlaylistId, m.PlaylistName, 0);
                existingItem.IsChecked = true;
            }
        }

        HasNetEase = NetEaseItems.Count > 0;
        HasQq = QqItems.Count > 0;
        HasLocal = LocalItems.Count > 0;
        OnPropertyChanged(nameof(CanConfirm));
        OnPropertyChanged(nameof(HasNothing));
        OnPropertyChanged(nameof(TitleText));
        OnPropertyChanged(nameof(ConfirmText));
        OnPropertyChanged(nameof(NamePlaceholder));
    }

    private AggregatePickerItemViewModel AddItem(ObservableCollection<AggregatePickerItemViewModel> list,
        MusicSource source, long id, string name, int trackCount)
    {
        var item = new AggregatePickerItemViewModel(source, id, name, trackCount);
        // 任一勾选变化 → 刷新确认按钮可用态
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AggregatePickerItemViewModel.IsChecked))
                OnPropertyChanged(nameof(CanConfirm));
        };
        list.Add(item);
        return item;
    }

    private AggregatePickerItemViewModel AddLocalItem(string localPlaylistId, string name, int trackCount)
    {
        var item = new AggregatePickerItemViewModel(localPlaylistId, name, trackCount);
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AggregatePickerItemViewModel.IsChecked))
                OnPropertyChanged(nameof(CanConfirm));
        };
        LocalItems.Add(item);
        return item;
    }

    [RelayCommand]
    private void Confirm()
    {
        var picked = NetEaseItems.Concat(QqItems).Concat(LocalItems).Where(i => i.IsChecked).ToList();
        if (picked.Count == 0) return;
        var members = picked.Select(i => new AggregatePlaylistMember
        {
            Source = i.Source,
            PlaylistId = i.PlaylistId,
            LocalPlaylistId = i.LocalPlaylistId,
            PlaylistName = i.Name,
        }).ToList();

        if (_editing is { } editing)
        {
            // 编辑模式:保 Id/SourceOrder,换成员;名字留空保留现名
            var updated = new AggregatePlaylist
            {
                Id = editing.Id,
                Name = Name.Trim().Length > 0 ? Name.Trim() : editing.Name,
                SourceOrder = editing.SourceOrder,
                Members = members,
            };
            _onEditConfirm(updated);
        }
        else
        {
            var aggregate = new AggregatePlaylist
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = Name.Trim().Length > 0 ? Name.Trim() : AutoName(),
                Members = members,
            };
            _onConfirm(aggregate);
        }
    }

    /// <summary>未命名自动生成:"聚合歌单 N"(按现有个数顺延,尽力避免重名)。</summary>
    private string AutoName() => $"聚合歌单 {_appState.AggregatePlaylists.Count + 1}";
}

/// <summary>聚合歌单候选行:源 + 歌单 id + 展示名 + 曲目数 + 勾选状态。</summary>
public sealed partial class AggregatePickerItemViewModel : ViewModelBase
{
    /// <summary>本地音乐歌单专用构造(LocalPlaylistId 非空;PlaylistId 恒 0)。</summary>
    public AggregatePickerItemViewModel(string localPlaylistId, string name, int trackCount)
    {
        Source = MusicSource.Local;
        LocalPlaylistId = localPlaylistId;
        Name = name;
        TrackCountText = trackCount > 0 ? $"{trackCount} 首" : "";
        SourceLabel = "本地音乐";
    }

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

    /// <summary>本地音乐歌单 Id(仅 Source == MusicSource.Local 时有效)。</summary>
    public string? LocalPlaylistId { get; }

    public string Name { get; }

    /// <summary>来源显示名(分区内冗余,备用)。</summary>
    public string SourceLabel { get; }

    public string TrackCountText { get; }

    [ObservableProperty] private bool _isChecked;
}
