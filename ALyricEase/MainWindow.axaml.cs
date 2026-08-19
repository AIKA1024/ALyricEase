using System;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Transformation;
using Avalonia.VisualTree;
#if WINDOWS
using Avalonia.Win32;
#endif
using ALyricEase.Infrastructure;
using ALyricEase.ViewModels;
using ALyricEase.Views;

namespace ALyricEase;

public partial class MainWindow : Window
{
#if WINDOWS
    private Win32Properties.CustomWndProcHookCallback? _wndProcHook;
#endif

    public MainWindow()
    {
        InitializeComponent();
        // WindowDecorations="Full"+ExtendClientArea 后顶部 48px 是 OS 层 HTCAPTION 拖拽区
        // (播放详情页全屏遮罩盖住标题栏时仍可拖动窗口)。但该区域内的按钮文本(命中时最顶层元素)
        // 会被当作拖拽,须标记 Win32Properties.NonClientHitTestResult=HTClient 才能点击。
        // Avalonia 12.1.1 无公开 API(chrome:ElementRole 在原生标题栏命中路径不生效且 XAML 编译报
        // AVLN3000;Win32Properties 的值类型 HitTestValues 是 internal),故用反射设置。
#if WINDOWS
        MarkClientHitTest(BackGlyph);
        MarkClientHitTest(MinGlyph);
        MarkClientHitTest(MaxGlyph);
        MarkClientHitTest(CloseGlyph);
        _wndProcHook = OnWndProc;
        Win32Properties.AddWndProcHookCallback(this, _wndProcHook);
#endif
        SizeChanged += OnSizeChanged;
        Opened += OnOpened;
        PropertyChanged += OnWindowPropertyChanged;
        // 覆盖层常驻:先把 RenderTransform 无过渡地摆到屏幕外(启动首帧即隐藏)。
        // 必须用代码且在 attach 前:若靠绑定首设值,attach 时会触发过渡导致启动闪现。
        SetOverlayTransformNoTransition(TranslateY(NowPlayingClosedY));
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        ResponsiveClasses.Apply(this, ClientSize.Width);
        // 布局完成、ClientSize 有效:把屏幕外位置从"大值兜底"更新为真实高度(仍无过渡,不可见)。
        SetOverlayTransformNoTransition(TranslateY(NowPlayingClosedY));
        UpdateFullScreenChrome();
        UpdateMaximizeGlyph();
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == WindowStateProperty)
        {
            UpdateFullScreenChrome();
            UpdateMaximizeGlyph();
        }
    }

    /// <summary>全屏时隐藏右上角窗口控制按钮,并禁止标题栏拖拽。</summary>
    private void UpdateFullScreenChrome()
    {
        var isFullScreen = WindowState == WindowState.FullScreen;
        if (WindowControls is not null)
            WindowControls.IsVisible = !isFullScreen;
        // 全屏禁用拖动由 OnWndProc 的 WM_NCHITTEST 处理,这里不再改动 TitleBar 的命中结果,
        // 避免影响正常状态下右上角按钮的点击。
    }

    /// <summary>根据窗口状态切换最大化/还原图标。</summary>
    private void UpdateMaximizeGlyph()
    {
        if (MaxGlyph is not null)
            MaxGlyph.Text = WindowState == WindowState.Maximized ? "" : "";
    }

#if WINDOWS
    /// <summary>全屏时把 Win32 命中测试直接改为 HTCLIENT,阻止系统标题栏拖动。</summary>
    private IntPtr OnWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_NCHITTEST = 0x0084;
        if (msg == WM_NCHITTEST && WindowState == WindowState.FullScreen)
        {
            handled = true;
            return new IntPtr(1); // HTCLIENT
        }

        return IntPtr.Zero;
    }

    /// <summary>把 HTCAPTION 拖拽带内的元素标记为 HTClient(可点击)。
    /// AOT 下用 DynamicDependency 保留 Win32Properties(类型可 typeof);
    /// HitTestValues 枚举在 Avalonia.Win32 程序集,由 TrimmerRoots.xml 保留(字符串 DynamicDependency 无法解析)。</summary>
    [DynamicDependency("SetNonClientHitTestResult", typeof(Win32Properties))]
    private static void MarkClientHitTest(Visual visual)
    {
        var assembly = typeof(Win32Properties).Assembly;
        var htType = assembly.GetType("Avalonia.Win32.Interop.UnmanagedMethods+HitTestValues");
        if (htType is null) return;
        var htClient = Enum.Parse(htType, "HTClient");
        var setter = typeof(Win32Properties).GetMethod("SetNonClientHitTestResult");
        setter?.Invoke(null, [visual, htClient]);
    }
#endif

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        ResponsiveClasses.Apply(this, e.NewSize.Width);
        // 关闭态(覆盖层在屏幕外)时随窗口高度同步屏幕外位置;打开态保持原位即可。
        if (!NowPlayingOverlay.IsHitTestVisible)
            SetOverlayTransformNoTransition(TranslateY(NowPlayingClosedY));
    }

    /// <summary>分组标题(发现/我的歌单)虽然和普通项在同一个 ListBox 中，但不允许被选中或高亮。
    /// 虚拟化会回收容器，因此每次准备时统一重设启用状态，避免禁用状态残留到普通项。</summary>
    private void OnShellNavContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container is ListBoxItem { DataContext: NavItemViewModel vm } item)
            item.IsEnabled = !vm.IsHeader;
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (WindowState == WindowState.FullScreen) return; // 全屏下不允许拖标题栏移动窗口

        // 按钮(返回/最小化/最大化/关闭)自己处理点击,冒泡到此不做窗口拖拽
        if (e.Source is Visual v && v.FindAncestorOfType<Button>() is not null) return;

        BeginMoveDrag(e);
    }

    private void OnShellPlaylistDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is ViewModels.MainViewModel vm && vm.Playlist.SelectedPlaylist is { } playlist)
            vm.OpenShellPlaylistCommand.Execute(playlist);
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        UpdateMaximizeGlyph();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
        => Close();

    /// <summary>图标栏(侧边栏收起态)点击导航:紧凑 ListBox 的 SelectedItem 特意是 OneWay
    /// (只把 VM 选中态同步进来,不回写——否则收起态打开歌单页时,歌单子项不在 CompactNavItems,
    /// ListBox 清选会把 VM 的 SelectedNav 冲掉,展开后歌单项高亮丢失)。点击由这里手动调
    /// NavigateCompactCommand 回写 VM;同引用回写是 SetProperty 无操作,不会与 OneWay 同步成环。</summary>
    private void OnCompactNavSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainViewModel vm
            && e.AddedItems?.Count > 0
            && e.AddedItems[0] is NavItemViewModel { IsItem: true } item)
            vm.NavigateCompactCommand.Execute(item);
    }

    /// <summary>侧边栏"账号":未登录 → 弹 Cookie 登录窗口;已登录 → 进账号占位页。</summary>
    private void OnAccountClick(object? sender, RoutedEventArgs e)
    {
        if (!ServiceLocator.Get<PlaylistViewModel>().IsLoggedIn)
        {
            new LoginWindow().ShowDialog(this);
            return;
        }
        (DataContext as MainViewModel)?.GoAccountCommand.Execute(null);
    }

    // ── 正在播放覆盖层滑入/滑出 ──────────────────────────────────────────────
    // 覆盖层常驻:关闭时整体下移到窗口下方屏幕外(不隐藏),过渡本身声明在 MainWindow.axaml。
    // 这里只负责在"原位(0)/屏幕外(窗口高度)"间切换 RenderTransform 目标值,过渡自动播放。
    // 不用 TransitioningContentControl:它空→有内容时 UpdateContent 先把新页置为可见、PageSlide 延迟一帧
    // 才应用起点偏移(开屏闪现),且关闭时 IsVisible 先隐藏、退出动画没机会跑。

    private static readonly double s_offScreenGuard = 100000;
    private const double s_slideMargin = 8;

    private MainViewModel? _nowPlayingVm;

    /// <summary>关闭态目标 Y:覆盖层下移一个窗口高度到屏幕外(留余量防亚像素露边);未布局时用大值兜底。</summary>
    private double NowPlayingClosedY
    {
        get
        {
            var h = ClientSize.Height;
            return (h > 0 ? h : s_offScreenGuard) + s_slideMargin;
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_nowPlayingVm is not null)
        {
            _nowPlayingVm.PropertyChanged -= OnNowPlayingVmPropertyChanged;
            _nowPlayingVm.Player.LoginRequired -= OnPlayerLoginRequired;
        }
        _nowPlayingVm = DataContext as MainViewModel;
        if (_nowPlayingVm is not null)
        {
            _nowPlayingVm.PropertyChanged += OnNowPlayingVmPropertyChanged;
            _nowPlayingVm.Player.LoginRequired += OnPlayerLoginRequired;
        }
    }

    private void OnNowPlayingVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ShowNowPlaying) && _nowPlayingVm is not null)
            NowPlayingOverlay.RenderTransform = TranslateY(_nowPlayingVm.ShowNowPlaying ? 0 : NowPlayingClosedY);
    }

    /// <summary>红心等账号功能未登录时触发:弹 Cookie 登录窗口。</summary>
    private void OnPlayerLoginRequired()
        => new LoginWindow().ShowDialog(this);

    /// <summary>临时停用 Transitions 设置基值(启动/缩放等时机先摆到屏幕外,不触发过渡)。</summary>
    private void SetOverlayTransformNoTransition(TransformOperations transform)
    {
        var transitions = NowPlayingOverlay.Transitions;
        NowPlayingOverlay.Transitions = null;
        NowPlayingOverlay.RenderTransform = transform;
        NowPlayingOverlay.Transitions = transitions;
    }

    /// <summary>构造 translate(0, y)(避免字符串解析的文化差异/格式问题)。</summary>
    private static TransformOperations TranslateY(double y)
    {
        var builder = TransformOperations.CreateBuilder(1);
        builder.AppendTranslate(0, y);
        return builder.Build();
    }
}
