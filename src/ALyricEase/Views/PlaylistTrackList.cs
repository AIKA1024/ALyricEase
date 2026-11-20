using System;
using Avalonia.Controls;

namespace ALyricEase.Views;

/// <summary>让虚拟化面板直接回收歌曲行及其模板，滚动时只更换行的数据。</summary>
public sealed class PlaylistTrackList : ItemsControl
{
    protected override Type StyleKeyOverride => typeof(ItemsControl);

    protected override bool NeedsContainerOverride(object? item, int index, out object? recycleKey)
        => NeedsContainer<TrackRow>(item, out recycleKey);

    protected override Control CreateContainerForItemOverride(object? item, int index, object? recycleKey)
        => new TrackRow();
}
