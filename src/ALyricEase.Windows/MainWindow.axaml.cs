using System;
using System.Numerics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Animations;
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
    private NowPlayingOverlayController? _nowPlayingController;
    private MainViewModel? _vm;

    /// <summary>常规态几何快照(仅 Normal 态更新;最大化/全屏/最小化时保留旧值供关闭落盘)。</summary>
    private double _normalWidth, _normalHeight;
    private int _normalX, _normalY;
    private bool _hasNormalBounds;

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
        MarkClientHitTest(TitleBarHamburgerGlyph);
        MarkClientHitTest(MinGlyph);
        MarkClientHitTest(MaxGlyph);
        MarkClientHitTest(CloseGlyph);
        _wndProcHook = OnWndProc;
        Win32Properties.AddWndProcHookCallback(this, _wndProcHook);
#endif
        SizeChanged += OnSizeChanged;
        Opened += OnOpened;
        PropertyChanged += OnWindowPropertyChanged;
        PositionChanged += OnWindowPositionChanged;
        Closing += OnMainWindowClosing;
        // 在窗口隧道路由阶段保留鼠标“后退”侧键，确保指针位于列表或弹层上时也能返回。
        AddHandler(InputElement.PointerPressedEvent, OnWindowPointerPressed, RoutingStrategies.Tunnel);
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        ResponsiveClasses.Apply(this, ClientSize.Width);
        UpdateTitleBarHamburgerVisibility();
        UpdateFullScreenChrome();
        UpdateMaximizeGlyph();
        // 布局完成、ClientSize 有效:把覆盖层"屏幕外"位置从兜底值更新为真实高度(仍无过渡,不可见)。
        // ⚠ 壳层此刻还没挂载 → 控制器为 null,挂载后(AttachShell)会再补一次。
        _nowPlayingController?.UpdateClosedPosition();

        // 启动画面已在首帧可见(合成线程旋转指示不受 UI 阻塞影响):
        // 再过两帧让启动画面确实呈现,然后挂载重内容壳层并收起启动画面。
        StartSplashSpinner();
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            AttachShell();
            return;
        }
        var remaining = 2;
        void Tick(TimeSpan _)
        {
            if (--remaining > 0) topLevel.RequestAnimationFrame(Tick);
            else AttachShell();
        }
        topLevel.RequestAnimationFrame(Tick);
    }

    private MainWindowShell? _shell;

    /// <summary>首帧渲染后挂载重内容壳层(标题栏之外的 AppShell/覆盖层/对话框)并收起启动画面。
    /// NowPlayingOverlayController 依赖壳内的 NowPlayingOverlay,在挂载后才建立。</summary>
    private void AttachShell()
    {
        if (_shell is not null) return;
        _shell = new MainWindowShell { DataContext = DataContext };
        ShellHost.Content = _shell;
        ShellHost.IsVisible = true;
        SplashPane.IsVisible = false;
        EnsureNowPlayingController();
        // 壳层刚挂载、布局未落定:补覆盖层屏幕外位置与标题栏按钮可见性
        _nowPlayingController?.UpdateClosedPosition();
        UpdateTitleBarHamburgerVisibility();
        UpdateFullScreenChrome();
        UpdateMaximizeGlyph();
    }

    /// <summary>启动画面旋转指示:合成线程驱动,UI 线程被壳层构建阻塞时照样转。</summary>
    private void StartSplashSpinner()
    {
        if (ElementComposition.GetElementVisual(Spinner) is not { } visual) return;
        visual.CenterPoint = new Vector3(11f, 11f, 0);
        var rotation = visual.Compositor.CreateScalarKeyFrameAnimation();
        rotation.Duration = TimeSpan.FromMilliseconds(900);
        rotation.IterationBehavior = AnimationIterationBehavior.Forever;
        rotation.InsertKeyFrame(0f, 0f);
        rotation.InsertKeyFrame(1f, MathF.PI * 2f);
        visual.StartAnimation("RotationAngle", rotation);
    }

    private void EnsureNowPlayingController()
    {
        if (_shell is null || _vm is null || _nowPlayingController is not null) return;
        _nowPlayingController = new NowPlayingOverlayController(_shell.NowPlayingOverlay, _vm);
    }

    /// <summary>在窗口首次显示前恢复上次会话的大小/位置/最大化，避免默认窗口首帧闪现。
    /// 位置仅当左上角落在任一显示器内才采用，否则交给系统选择初始位置。</summary>
    internal void RestorePersistedWindowBounds()
    {
        var st = _vm?.AppState;
        if (st is null) return;
        if (st.WindowWidth is { } w && w is >= 320 and <= 7680) Width = w;
        if (st.WindowHeight is { } h && h is >= 240 and <= 4320) Height = h;
        if (st.WindowX is { } x && st.WindowY is { } y)
        {
            var pos = new PixelPoint(x, y);
            foreach (var s in Screens.All)
            {
                if (!s.Bounds.Contains(pos)) continue;
                WindowStartupLocation = WindowStartupLocation.Manual;
                Position = pos;
                break;
            }
        }
        // 最大化前保留已恢复的常规态几何，关闭时仍可正确落盘。
        TrackNormalBounds();
        if (st.WindowMaximized) WindowState = WindowState.Maximized;
    }

    /// <summary>仅在常规态记录当前几何(最大化/全屏/最小化跳过,快照保留上一个常规态)。</summary>
    private void TrackNormalBounds()
    {
        if (WindowState != WindowState.Normal) return;
        _normalWidth = Width;
        _normalHeight = Height;
        var p = Position;
        _normalX = p.X;
        _normalY = p.Y;
        _hasNormalBounds = true;
    }

    /// <summary>关闭时把折叠状态之外的窗口几何落盘(state.json 原子小文件,同步写无感)。</summary>
    private void OnMainWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        TrackNormalBounds();
        var st = _vm?.AppState;
        if (st is null || !_hasNormalBounds) return;
        st.WindowWidth = _normalWidth;
        st.WindowHeight = _normalHeight;
        st.WindowX = _normalX;
        st.WindowY = _normalY;
        st.WindowMaximized = WindowState == WindowState.Maximized;
        st.Save();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _vm = DataContext as MainViewModel;
        _nowPlayingController?.Dispose();
        _nowPlayingController = null;
        // 控制器依赖壳内的 NowPlayingOverlay:壳挂载前(_shell null)先不建,AttachShell 时补建
        EnsureNowPlayingController();
    }

    private void UpdateTitleBarHamburgerVisibility()
    {
        if (TitleBarHamburger is not null)
            TitleBarHamburger.IsVisible = ClientSize.Width > 0 && ClientSize.Width < ResponsiveClasses.NarrowWidth;
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == WindowStateProperty)
        {
            UpdateFullScreenChrome();
            UpdateMaximizeGlyph();
            // 离开最大化回常规:新几何随后的 Size/Position 事件再刷新快照
            TrackNormalBounds();
        }
    }

    private void OnWindowPositionChanged(object? sender, PixelPointEventArgs e)
        => TrackNormalBounds();

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
            MaxGlyph.Text = WindowState == WindowState.Maximized ? "" : "";
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
        TrackNormalBounds();
        ResponsiveClasses.Apply(this, e.NewSize.Width);
        UpdateTitleBarHamburgerVisibility();
        // 关闭态(覆盖层在屏幕外)时随窗口高度同步屏幕外位置;打开态保持原位即可。
        _nowPlayingController?.UpdateClosedPosition();
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (WindowState == WindowState.FullScreen) return; // 全屏下不允许拖标题栏移动窗口

        // 按钮(返回/最小化/最大化/关闭)自己处理点击,冒泡到此不做窗口拖拽
        if (e.Source is Visual v && v.FindAncestorOfType<Button>() is not null) return;

        BeginMoveDrag(e);
    }

    /// <summary>鼠标 XButton1（常见的“后退”侧键）复用应用统一返回语义。</summary>
    private void OnWindowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Pointer.Type != PointerType.Mouse
            || !e.GetCurrentPoint(this).Properties.IsXButton1Pressed)
            return;

        // 即使当前没有可返回内容也消费该专用按键，避免它落到列表项等普通控件上触发交互。
        e.Handled = true;
        _vm?.TryHandleBack();
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

}
