using System;
using System.ComponentModel;
using System.Numerics;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Rendering.Composition;
using ALyricEase.Infrastructure;
using ALyricEase.Services;
using ALyricEase.ViewModels;

namespace ALyricEase.Views;

/// <summary>共享主界面壳:导航(宽/紧凑/抽屉) + 内容区 + 播放条。
/// Desktop 的 MainWindow 和 Android 的 MainView 都承载这个壳。
/// 正在播放覆盖层与登录弹层是窗口级元素(桌面端要盖住标题栏),不在本壳内,
/// 由宿主(MainWindow/MainView)直接挂载并由 NowPlayingOverlayController / LoginDialogView 驱动。</summary>
public partial class AppShell : UserControl
{
    private MainViewModel? _vm;

    // 抽屉滑动的合成动画状态:当前逻辑位(开=0 / 关=-320)。属性 getter 不反映动画末帧,
    // 起点必须自维护(同 NowPlayingOverlayController 的 _current)。
    private Vector3 _drawerCurrent = new(-320f, 0f, 0f);
    // 抽屉上次的开合状态:null=未初始化(首帧/刚离开宽屏,只钉位不播动画)
    private bool? _lastDrawerOpen;

    public AppShell()
    {
        InitializeComponent();
        SizeChanged += OnSizeChanged;
        DataContextChanged += OnDataContextChanged;
        AttachedToVisualTree += (_, _) =>
        {
            ResponsiveClasses.Apply(this, Bounds.Width);
            UpdateNavigationVisibility();
        };
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            _vm.Player.LoginRequired -= OnPlayerLoginRequired;
            _vm.Playlist.PropertyChanged -= OnPlaylistPropertyChanged;
        }

        _vm = DataContext as MainViewModel;

        if (_vm is not null)
        {
            _vm.PropertyChanged += OnViewModelPropertyChanged;
            _vm.Player.LoginRequired += OnPlayerLoginRequired;
            _vm.Playlist.PropertyChanged += OnPlaylistPropertyChanged;
            UpdateNavigationVisibility();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsNavigationExpanded)
            or nameof(MainViewModel.IsNavigationDrawerOpen))
            UpdateNavigationVisibility();
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        ResponsiveClasses.Apply(this, e.NewSize.Width);
        UpdateNavigationVisibility();
    }

    /// <summary>当前是否移动端(Android)。移动端 banner 汉堡按钮始终可见,以避免与侧栏汉堡同时出现。</summary>
    private static bool IsMobile => OperatingSystem.IsAndroid();

    /// <summary>侧边栏形态按宽度 + VM 状态切换。</summary>
    private void UpdateNavigationVisibility()
    {
        var width = Bounds.Width;
        bool wide = width >= ResponsiveClasses.CompactWidth;
        bool compact = width is >= ResponsiveClasses.NarrowWidth and < ResponsiveClasses.CompactWidth;
        bool narrow = width is > 0 and < ResponsiveClasses.NarrowWidth;
        var vm = DataContext as MainViewModel;
        bool expanded = vm?.IsNavigationExpanded ?? true;
        bool drawerOpen = vm?.IsNavigationDrawerOpen ?? false;

        WideSidebar.IsVisible = wide && expanded;
        CompactSidebar.IsVisible = (wide && !expanded) || compact;
        DrawerRoot.IsVisible = compact || narrow;
        // 移动端 banner 汉堡按钮(仿 Android Toolbar 顶部汉堡)始终可见；
        // 仅 compact(手机横屏)时与 banner 汉堡同为"展开抽屉"职责,冗余 → 隐藏侧栏汉堡。
        // 宽屏折叠态(expanded=false)的汉堡职责是"重新展开内联栏",与 banner 不同,保留。
        // 桌面端 title-bar 汉堡仅 narrow 时可见,compact 时无汉堡,因此必须保留侧栏汉堡。
        CompactHamburger.IsVisible = !(IsMobile && compact);
        DrawerRoot.IsHitTestVisible = drawerOpen;
        AnimateDrawer(compact || narrow, drawerOpen);
    }

    /// <summary>抽屉滑入/滑出:合成线程 Translation 动画(抽屉)+ Opacity 动画(遮罩)。
    /// 不用 UI 线程 Transitions:与壳层/详情页入场同理,UI 线程忙时过渡会掉帧。
    /// ⚠ 只在开合状态真正变化时播动画:本方法由每次尺寸变化/属性变化触发,若无条件重播,
    /// 关闭态的遮罩会反复重播 1→0 淡出而"一直闪动"(实测)。状态未变时只钉位。
    /// DrawerRoot 隐藏(宽屏)时不做任何动画,只记账。</summary>
    private void AnimateDrawer(bool rootVisible, bool open)
    {
        if (!rootVisible)
        {
            _drawerCurrent = new Vector3(0f, -320f, 0f);
            _lastDrawerOpen = null;
            return;
        }

        var drawer = ElementComposition.GetElementVisual(NavigationDrawer);
        var scrim = ElementComposition.GetElementVisual(DrawerScrim);
        if (drawer is null || scrim is null)
        {
            _lastDrawerOpen = null; // 未挂载:下次挂载后按钉位处理
            return;
        }

        // 状态未变(初始化/尺寸变化):只钉位,不播动画
        var animate = _lastDrawerOpen is bool prev && prev != open;
        _lastDrawerOpen = open;

        var to = new Vector3(open ? 0f : -320f, 0f, 0f);
        if (!animate)
        {
            drawer.StopAnimation("Translation");
            drawer.Translation = to;
            _drawerCurrent = to;
            scrim.StopAnimation("Opacity");
            scrim.Opacity = open ? 1f : 0f;
            return;
        }

        var from = _drawerCurrent;
        _drawerCurrent = to;

        // 基值先设为目标位(结束帧=基值,回落无缝),动画从旧位起播;同批次应用无闪帧
        drawer.Translation = to;
        var slide = drawer.Compositor.CreateVector3KeyFrameAnimation();
        slide.Duration = TimeSpan.FromMilliseconds(250);
        slide.InsertKeyFrame(0f, from);
        slide.InsertKeyFrame(1f, to, new SplineEasing(0.215, 0.61, 0.355, 1));
        drawer.StartAnimation("Translation", slide);

        var targetScrim = open ? 1f : 0f;
        scrim.Opacity = targetScrim;
        var fade = scrim.Compositor.CreateScalarKeyFrameAnimation();
        fade.Duration = TimeSpan.FromMilliseconds(200);
        fade.InsertKeyFrame(0f, open ? 0f : 1f);
        fade.InsertKeyFrame(1f, targetScrim);
        scrim.StartAnimation("Opacity", fade);
    }

    /// <summary>图标栏(侧边栏收起态)点击导航。</summary>
    private void OnCompactNavSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainViewModel vm
            && e.AddedItems?.Count > 0
            && e.AddedItems[0] is NavItemViewModel { IsItem: true } item)
            vm.NavigateCompactCommand.Execute(item);
    }

    /// <summary>侧边栏“账号”：未登录时打开登录对话框，任一平台已登录时进入账号页。</summary>
    private void OnAccountClick(object? sender, RoutedEventArgs e)
    {
        if (!ServiceLocator.Get<PlaylistViewModel>().HasAnyLogin)
        {
            (DataContext as MainViewModel)?.OpenLoginDialogCommand.Execute(null);
            return;
        }
        (DataContext as MainViewModel)?.GoAccountCommand.Execute(null);
    }

    /// <summary>中屏图标栏汉堡:宽屏收起态→内联展开,中/小屏→开抽屉。</summary>
    private void OnCompactHamburgerClick(object? sender, RoutedEventArgs e)
        => NavigationHamburger.Dispatch(this, DataContext as MainViewModel);

    /// <summary>抽屉遮罩点任意处关闭抽屉。</summary>
    private void OnDrawerScrimPointerPressed(object? sender, PointerPressedEventArgs e)
        => (DataContext as MainViewModel)?.CloseNavigationDrawerCommand.Execute(null);

    /// <summary>网易云或 QQ 音乐登录成功(对应 IsLoggedIn/IsQqLoggedIn 翻 true)自动关闭登录弹窗。</summary>
    private void OnPlaylistPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        var success = e.PropertyName switch
        {
            nameof(PlaylistViewModel.IsLoggedIn) => sender is PlaylistViewModel { IsLoggedIn: true },
            nameof(PlaylistViewModel.IsQqLoggedIn) => sender is PlaylistViewModel { IsQqLoggedIn: true },
            _ => false,
        };
        if (success && _vm is not null)
            _vm.IsLoginDialogOpen = false;
    }

    private void OnPlayerLoginRequired(MusicSource? source, string? expiredHint)
        => _vm?.OpenLoginDialogFor(source, expiredHint);
}
