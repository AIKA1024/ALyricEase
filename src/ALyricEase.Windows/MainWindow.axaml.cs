using System;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
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
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        RestorePersistedWindowBounds();
        ResponsiveClasses.Apply(this, ClientSize.Width);
        UpdateTitleBarHamburgerVisibility();
        UpdateFullScreenChrome();
        UpdateMaximizeGlyph();
        // 布局完成、ClientSize 有效:把覆盖层"屏幕外"位置从兜底值更新为真实高度(仍无过渡,不可见)。
        _nowPlayingController?.UpdateClosedPosition();
    }

    /// <summary>恢复上次会话的窗口大小/位置/最大化。Screens 需要平台句柄,故在 Opened 里做;
    /// 位置仅当左上角落在任一显示器内才采用,否则保持默认居中(防止换显示器后窗口丢失)。</summary>
    private void RestorePersistedWindowBounds()
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
                Position = pos;
                break;
            }
        }
        if (st.WindowMaximized) WindowState = WindowState.Maximized;
        TrackNormalBounds(); // 初始化快照,首次未动过的关闭也能落盘
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
        _nowPlayingController = DataContext is MainViewModel vm
            ? new NowPlayingOverlayController(NowPlayingOverlay, vm)
            : null;
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
