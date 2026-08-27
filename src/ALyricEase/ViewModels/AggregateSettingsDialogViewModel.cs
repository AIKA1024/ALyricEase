using System;
using ALyricEase.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ALyricEase.ViewModels;

/// <summary>聚合歌单设置对话框 VM:选择成员按来源的排列顺序(网易云在前/QQ在前),
/// 保存后交给宿主(MainViewModel)持久化并按新顺序重开当前聚合页。</summary>
public sealed partial class AggregateSettingsDialogViewModel : ViewModelBase
{
    private readonly Action<AggregatePlaylist> _onSaved;
    private AggregatePlaylist? _current;

    public AggregateSettingsDialogViewModel(Action<AggregatePlaylist> onSaved)
    {
        _onSaved = onSaved;
    }

    /// <summary>当前聚合歌单名(标题副文案)。</summary>
    [ObservableProperty] private string _aggregateName = "";

    /// <summary>单选:网易云在前。</summary>
    [ObservableProperty] private bool _netEaseFirst;

    /// <summary>单选:QQ在前。</summary>
    [ObservableProperty] private bool _qqFirst;

    /// <summary>打开对话框时调用:按当前设置初始化两个单选。</summary>
    public void Refresh(AggregatePlaylist aggregate)
    {
        _current = aggregate;
        AggregateName = aggregate.Name;
        NetEaseFirst = aggregate.SourceOrder != AggregateSourceOrder.QqFirst;
        QqFirst = aggregate.SourceOrder == AggregateSourceOrder.QqFirst;
    }

    [RelayCommand]
    private void Save()
    {
        if (_current is null) return;
        _current.SourceOrder = QqFirst ? AggregateSourceOrder.QqFirst : AggregateSourceOrder.NetEaseFirst;
        _onSaved(_current);
    }
}
