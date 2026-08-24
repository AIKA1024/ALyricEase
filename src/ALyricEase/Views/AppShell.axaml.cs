using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using ALyricEase.Infrastructure;
using ALyricEase.ViewModels;

namespace ALyricEase.Views;

/// <summary>共享主界面壳:导航(宽/紧凑/抽屉) + 内容区 + 播放条。
/// Desktop 的 MainWindow 和 Android 的 MainView 都承载这个壳。</summary>
public partial class AppShell : UserControl
{
    private MainViewModel? _vm;

    private static readonly double s_offScreenGuard = 100000;
    private const double s_slideMargin = 8;

    private double NowPlayingClosedY
    {
        get
        {
            var h = Bounds.Height;
            return (h > 0 ? h : s_offScreenGuard) + s_slideMargin;
        }
    }

    public AppShell()
    {
        InitializeComponent();
        SetOverlayTransformNoTransition(TranslateY(NowPlayingClosedY));
        SizeChanged += OnSizeChanged;
        DataContextChanged += OnDataContextChanged;
        AttachedToVisualTree += (_, _) =>
        {
            ResponsiveClasses.Apply(this, Bounds.Width);
            UpdateNavigationVisibility();
            SetOverlayTransformNoTransition(TranslateY(NowPlayingClosedY));
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

        if (e.PropertyName == nameof(MainViewModel.ShowNowPlaying) && _vm is not null)
            NowPlayingOverlay.RenderTransform = TranslateY(_vm.ShowNowPlaying ? 0 : NowPlayingClosedY);

        if (e.PropertyName == nameof(MainViewModel.IsLoginDialogOpen)
            && _vm is { IsLoginDialogOpen: true })
        {
            LoginDialogOverlay.Opacity = 0;
            Dispatcher.UIThread.Post(() =>
            {
                LoginDialogOverlay.Opacity = 1;
                MusicUBox.Focus();
            }, DispatcherPriority.Render);
        }
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        ResponsiveClasses.Apply(this, e.NewSize.Width);
        UpdateNavigationVisibility();
        if (!NowPlayingOverlay.IsHitTestVisible)
            SetOverlayTransformNoTransition(TranslateY(NowPlayingClosedY));
    }

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
        DrawerRoot.IsHitTestVisible = drawerOpen;
        DrawerScrim.Opacity = drawerOpen ? 1 : 0;
        NavigationDrawer.RenderTransform = drawerOpen ? TranslateX(0) : TranslateX(-320);
    }

    /// <summary>图标栏(侧边栏收起态)点击导航。</summary>
    private void OnCompactNavSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainViewModel vm
            && e.AddedItems?.Count > 0
            && e.AddedItems[0] is NavItemViewModel { IsItem: true } item)
            vm.NavigateCompactCommand.Execute(item);
    }

    /// <summary>侧边栏"账号":未登录 → 弹登录对话框;已登录 → 进账号占位页。</summary>
    private void OnAccountClick(object? sender, RoutedEventArgs e)
    {
        if (!ServiceLocator.Get<PlaylistViewModel>().IsLoggedIn)
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

    private void OnPlaylistPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlaylistViewModel.IsLoggedIn)
            && sender is PlaylistViewModel { IsLoggedIn: true }
            && _vm is not null)
            _vm.IsLoginDialogOpen = false;
    }

    private void OnPlayerLoginRequired()
        => _vm?.OpenLoginDialogCommand.Execute(null);

    private void SetOverlayTransformNoTransition(TransformOperations transform)
    {
        var transitions = NowPlayingOverlay.Transitions;
        NowPlayingOverlay.Transitions = null;
        NowPlayingOverlay.RenderTransform = transform;
        NowPlayingOverlay.Transitions = transitions;
    }

    private static TransformOperations TranslateY(double y)
    {
        var builder = TransformOperations.CreateBuilder(1);
        builder.AppendTranslate(0, y);
        return builder.Build();
    }

    private static TransformOperations TranslateX(double x)
    {
        var builder = TransformOperations.CreateBuilder(1);
        builder.AppendTranslate(x, 0);
        return builder.Build();
    }
}
