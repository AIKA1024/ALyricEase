using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using ALyricEase.Infrastructure;
using Avalonia.Input;
using ALyricEase.ViewModels;

namespace ALyricEase.Views;

public partial class SearchView : UserControl
{
    public SearchView()
    {
        InitializeComponent();
        SizeChanged += (_, e) => ResponsiveClasses.ApplyByWindow(this);
        AttachedToVisualTree += (_, _) => ResponsiveClasses.ApplyByWindow(this);
    }

    /// <summary>结果行容器准备完成(DataContext 已赋值)时触发,触发懒加载封面。
    /// 注意:用 ContainerPrepared 而非 PreparingContainer — 后者在 DataContext 赋值之前触发,
    /// 此时 e.Container.DataContext 仍是 null/旧值,EnsureCoverLoaded 永远调不到当前项,
    /// 表现为封面始终是占位纯色(其它页 PlaylistView/NowPlayingView 等正确用了 ContainerPrepared)。</summary>
    private void OnResultContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container?.DataContext is SongItemViewModel item)
            item.EnsureCoverLoaded();
    }

    private void OnListDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is SearchViewModel vm && vm.SelectedItem is { } item)
            item.PlayCommand.Execute(null);
    }
}
