using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.ViewModels;

namespace ALyricEase.Views;

/// <summary>用户页"关注 / 粉丝"名单页:滚动近底部流式补页,点行进入对方用户页。</summary>
public partial class UserRelationsPageView : UserControl
{
    private readonly DetailPageScrollController _scrollController;

    public UserRelationsPageView()
    {
        InitializeComponent();
        _scrollController = new DetailPageScrollController(this, PageScroller);
        SizeChanged += (_, e) => ResponsiveClasses.ApplyByWindow(this);
        AttachedToVisualTree += (_, _) => ResponsiveClasses.ApplyByWindow(this);
        PageScroller.ScrollChanged += OnScrollChanged;
    }

    /// <summary>滚动接近底部(剩余不足 ~500px)时请求下一页;LoadMoreAsync 内部单飞防止重入。
    /// 守卫:内容未溢出视口(初始 0 高度/首页未填满)时不触发,避免布局期连续 ScrollChanged
    /// 把分页链一路跑到底。</summary>
    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        if (DataContext is not UserRelationsPageViewModel vm) return;
        if (!vm.HasMore || vm.IsLoadingMore) return;
        if (sv.Viewport.Height <= 0 || sv.Extent.Height <= sv.Viewport.Height) return;
        var remaining = sv.Extent.Height - sv.Offset.Y - sv.Viewport.Height;
        if (remaining < 500)
            _ = vm.LoadMoreAsync();
    }

    /// <summary>点名单里的一行 → 对方用户页(QQ 的歌手条目进歌手页)。激活走 Tapped 手势层
    /// (TrackRow 同款):触屏长按后手势层不发 Tapped,不会像 Click 那样在松手时补发激活。</summary>
    private void OnUserRowTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Button { DataContext: UserRelationItemViewModel row }) return;
        e.Handled = true;
        var main = ServiceLocator.Get<MainViewModel>();
        if (row.IsSinger)
            main.OpenQqArtistCommand.Execute(row.SingerMid);
        else if (row.Source == MusicSource.QQ)
            main.OpenQqUserCommand.Execute(row.EncryptUin);
        else
            main.OpenUserCommand.Execute(row.UserId);
    }
}
