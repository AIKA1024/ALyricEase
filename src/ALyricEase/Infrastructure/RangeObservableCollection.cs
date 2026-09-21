using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace ALyricEase.Infrastructure;

/// <summary>支持一次集合通知追加多个项目，避免大量逐行 Add 触发布局与容器生成抖动。</summary>
public sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    public void AddRange(IReadOnlyList<T> items)
    {
        if (items.Count == 0) return;

        var startIndex = Count;
        foreach (var item in items)
            Items.Add(item);

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        var changedItems = items as IList ?? items.ToList();
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(
            NotifyCollectionChangedAction.Add, changedItems, startIndex));
    }

    /// <summary>一次通知替换集合内容，适合排序或筛选后的完整投影。</summary>
    public void ReplaceAll(IReadOnlyList<T> items)
    {
        Items.Clear();
        foreach (var item in items)
            Items.Add(item);

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <summary>一次通知删除一段(逐项 <c>RemoveAt</c> 会产生同样多次集合通知与布局失效)。
    /// 歌单页收窄到网络前缀时尾部可能一次要摘掉几百行,逐项删除在长列表上不划算。</summary>
    public void RemoveRange(int startIndex, int count)
    {
        if (count <= 0) return;
        if (startIndex < 0 || startIndex + count > Count) throw new ArgumentOutOfRangeException(nameof(count));

        var removed = new List<T>(count);
        for (var i = 0; i < count; i++)
        {
            removed.Add(Items[startIndex]);
            Items.RemoveAt(startIndex);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(
            NotifyCollectionChangedAction.Remove, removed, startIndex));
    }
}
