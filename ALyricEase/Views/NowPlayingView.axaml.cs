using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Transformation;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ALyricEase.Infrastructure;
using ALyricEase.ViewModels;

namespace ALyricEase.Views;

/// <summary>LyricEase 式正在播放页。仿原版 PlaybackDetailView:Canvas 绝对定位 + 代码后置布局,
/// 控件位置由 UpdateDesktopLayout 按 尺寸档(小 &lt;700 / 中 700~1199 / 大 ≥1200)×面板状态 计算;
/// RenderTransform 负责实际定位/命中测试;Composition Translation 负责 0.3s 补间动画,
/// 缩放窗口/开关面板时控件平滑漂移。
/// 布局规则(截图+用户确认):大/小屏无面板信息在封面下方;中屏信息在窗口顶部;开面板时封面缩为 60px 迷你头。</summary>
public partial class NowPlayingView : UserControl
{
    // 全屏按钮图标:进入全屏(E924)/退出全屏(E923),与原版 Fluent 字体一致
    private const string EnterFullScreenGlyph = "\uE924";
    private const string ExitFullScreenGlyph = "\uE923";

    // 大屏封面下方的控件栈高度:信息54+进度52+控制60+次级50+切换32 + 间距 22+18+14+16+24 = 342
    private const double BelowCoverStack = 342;
    private const double PanelGap = 42;   // 大屏:播放列与右侧面板间距

    private static readonly TimeSpan s_slideDuration = TimeSpan.FromSeconds(0.3);
    private static readonly SplineEasing s_slideEase = new(0.215, 0.61, 0.355, 1); // 与主窗口覆盖层同曲线

    private Window? _window;
    private MainViewModel? _vm;
    private bool _transitionsAttached;
    private readonly Dictionary<Control, Point> _positions = new();

    public NowPlayingView()
    {
        InitializeComponent();
        SizeChanged += OnSizeChanged;
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        DataContextChanged += OnDataContextChanged;
        // Escape 由收起按钮的 HotKeyManager.HotKey 全局处理(XAML),不依赖本视图焦点;
        // 聚焦(OnShowNowPlaying)仅为进度条方向键等内部键盘交互争取焦点,失败不影响 Escape。
    }

    /// <summary>覆盖层常驻(关闭时在屏幕外),打开(ShowNowPlaying=true)时聚焦本视图。</summary>
    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnViewModelPropertyChanged;
        _vm = DataContext as MainViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not MainViewModel vm) return;
        if (e.PropertyName is nameof(MainViewModel.ShowNowPlaying) && vm.ShowNowPlaying)
            Dispatcher.UIThread.Post(() => Focus(), DispatcherPriority.Background);
        else if (e.PropertyName is nameof(MainViewModel.NowPlayingPanel))
            UpdateDesktopLayout(); // 面板开关 → 重算布局(控件漂移过去)
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        ResponsiveClasses.Apply(this, e.NewSize.Width);
        UpdateDesktopLayout();
    }

    /// <summary>收起按钮:常规顶部居中;中屏无面板时信息区占窗口正上方,故挪到左上(原版行为)。</summary>
    private void PlaceCollapseButton(double w, bool topLeft)
        => MoveTo(CollapseButton, topLeft ? 12 : w / 2 - 25, 16);

    /// <summary>信息区文本对齐:中屏无面板时居中(窗口正上方),其余状态左对齐。</summary>
    private void SetInfoCentered(bool centered)
    {
        var alignment = centered ? Avalonia.Media.TextAlignment.Center : Avalonia.Media.TextAlignment.Left;
        TitleText.TextAlignment = alignment;
        ArtistText.TextAlignment = alignment;
    }

    // ── 响应式布局引擎 ──────────────────────────────────────────────────────

    private void UpdateDesktopLayout()
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;
        var panelOpen = _vm?.NowPlayingPanel is not NowPlayingPanel.None;

        if (w >= 1200) LayoutLarge(w, h, panelOpen);
        else if (w >= 700) LayoutMedium(w, h, panelOpen);
        else LayoutSmall(w, h, panelOpen);

        // 首次布局完成后再启用 Composition 动画:启动/首开时不应看到从 (0,0) 漂移的动画
        if (!_transitionsAttached)
            Dispatcher.UIThread.Post(() => _transitionsAttached = true, DispatcherPriority.Loaded);
    }

    /// <summary>大屏(≥1200,1920 截图基准):播放列 = 封面+信息+进度+控制+次级+切换;
    /// 无面板居中;开面板整组左移,面板占剩余宽度(1920 时恰为 898)。</summary>
    private void LayoutLarge(double w, double h, bool panelOpen)
    {
        var margin = Math.Clamp(w * 0.10, 36, 190);
        var cover = Math.Clamp(Math.Min(h - BelowCoverStack - 114, w - margin * 2), 180, 600);
        var colH = cover + BelowCoverStack;
        var colTop = (h - colH) / 2 + 35;
        double colX;
        if (panelOpen)
        {
            colX = margin;
            var panelW = Math.Max(280, w - margin * 2 - cover - PanelGap);
            SetRect(LyricsPanel, colX + cover + PanelGap, colTop, panelW, colH);
            SetRect(QueuePanel, colX + cover + PanelGap, colTop, panelW, colH);
        }
        else
        {
            colX = (w - cover) / 2;
        }
        Artwork.CornerRadius = new CornerRadius(16);
        PlaceFullColumn(colX, colTop, cover);
        PlaceCollapseButton(w, topLeft: false);
        SetInfoCentered(false);
        SetVisibility(progress: true, prev: true, play: true, next: true, like: true, mode: true, volume: true, queueToggle: true);
    }

    /// <summary>中屏(700~1199):信息在窗口顶部。无面板:顶部信息 + 居中中图 + 完整控件栈;
    /// 开面板:封面缩为左上迷你头,信息在其右,面板撑满中部,控件栈隐藏只留底部切换行。</summary>
    private void LayoutMedium(double w, double h, bool panelOpen)
    {
        var margin = Math.Clamp(w * 0.06, 24, 64);
        if (!panelOpen)
        {
            // 信息在窗口正上方(水平居中);收起按钮让位到左上
            var infoW = Math.Min(w - margin * 2, 420);
            SetRect(InfoPanel, (w - infoW) / 2, 16, infoW, 54);
            PlaceCollapseButton(w, topLeft: true);
            SetInfoCentered(true);
            var cover = Math.Clamp(Math.Min(h - 560, w * 0.45), 160, 380);
            var colX = (w - cover) / 2;
            const double below = 266; // 进度52+14+控制60+16+次级50+24+切换32+间距18
            var colTop = 164 + Math.Max(0, (h - 164 - below - cover) / 2);
            Artwork.CornerRadius = new CornerRadius(16);
            SetRect(Artwork, colX, colTop, cover, cover);
            var center = colX + cover / 2;
            var y = colTop + cover + 18;
            SetRect(ProgressArea, colX, y, cover, 52); y += 52 + 14;
            MoveTo(PrevButton, center - 110, y + 5); MoveTo(PlayButton, center - 30, y); MoveTo(NextButton, center + 60, y + 5);
            y += 60 + 16;
            MoveTo(ModeButton, colX, y); MoveTo(LikeButton, center - 25, y); MoveTo(VolumeButton, colX + cover - 50, y);
            y += 50 + 24;
            MoveTo(LyricsToggle, colX, y); MoveTo(FullScreenToggle, center - 24, y); MoveTo(QueueToggle, colX + cover - 48, y);
            SetVisibility(progress: true, prev: true, play: true, next: true, like: true, mode: true, volume: true, queueToggle: true);
        }
        else
        {
            PlaceMiniHeader(margin, w);
            PlaceCollapseButton(w, topLeft: false);
            SetInfoCentered(false);
            var panelTop = 164.0;
            SetRect(LyricsPanel, margin, panelTop, w - margin * 2, h - panelTop - 76);
            SetRect(QueuePanel, margin, panelTop, w - margin * 2, h - panelTop - 76);
            PlaceBottomToggles(margin, w, h, queue: true);
            SetVisibility(progress: false, prev: false, play: false, next: false, like: false, mode: false, volume: false, queueToggle: true);
        }
    }

    /// <summary>小屏(&lt;700,502x954 截图基准)。无面板:与大屏相同的完整控件栈,但各行横跨整列(边距 42),
    /// 封面居中收窄;控件栈锚定底部,封面锚定顶部(0.17h),中间空隙留给封面→信息区。
    /// 开面板:左上迷你头 + 面板撑满 + 底部行(歌词/全屏/播放列表)。</summary>
    private void LayoutSmall(double w, double h, bool panelOpen)
    {
        if (!panelOpen)
        {
            var rowX = 42.0;
            var rowW = w - 84;
            // 控件栈锚定底部(切换行底边距窗底 ~37);封面锚定 0.17h;封面→信息区间距吃掉剩余空间
            var togglesY = h - 69;
            var secondaryY = togglesY - 32 - 50;
            var transportY = secondaryY - 24 - 60;
            var progressY = transportY - 16 - 52;
            var infoY = progressY - 32 - 54;
            var coverTop = Math.Clamp(h * 0.17, 86, 165);
            var cover = Math.Clamp(Math.Min(rowW * 0.75, h * 0.36), 160, Math.Max(160, infoY - coverTop - 20));
            Artwork.CornerRadius = new CornerRadius(16);
            SetRect(Artwork, rowX + (rowW - cover) / 2, coverTop, cover, cover);
            var center = rowX + rowW / 2;
            SetRect(InfoPanel, rowX, infoY, rowW, 54);
            SetRect(ProgressArea, rowX, progressY, rowW, 52);
            MoveTo(PrevButton, center - 110, transportY + 5); MoveTo(PlayButton, center - 30, transportY); MoveTo(NextButton, center + 60, transportY + 5);
            MoveTo(ModeButton, rowX, secondaryY); MoveTo(LikeButton, center - 25, secondaryY); MoveTo(VolumeButton, rowX + rowW - 50, secondaryY);
            MoveTo(LyricsToggle, rowX, togglesY); MoveTo(FullScreenToggle, center - 24, togglesY); MoveTo(QueueToggle, rowX + rowW - 48, togglesY);
            PlaceCollapseButton(w, topLeft: false);
            SetInfoCentered(false);
            SetVisibility(progress: true, prev: true, play: true, next: true, like: true, mode: true, volume: true, queueToggle: true);
        }
        else
        {
            PlaceMiniHeader(42, w);
            PlaceCollapseButton(w, topLeft: false);
            SetInfoCentered(false);
            var panelTop = 160.0;
            SetRect(LyricsPanel, 33, panelTop, w - 66, h - panelTop - 70);
            SetRect(QueuePanel, 33, panelTop, w - 66, h - panelTop - 70);
            PlaceBottomToggles(42, w, h, queue: true);
            SetVisibility(progress: false, prev: false, play: false, next: false, like: false, mode: false, volume: false, queueToggle: true);
        }
    }

    /// <summary>大屏的完整播放列(封面 + 封面下方控件栈,各行与封面同宽)。</summary>
    private void PlaceFullColumn(double colX, double colTop, double cover)
    {
        var center = colX + cover / 2;
        SetRect(Artwork, colX, colTop, cover, cover);
        var y = colTop + cover + 22;
        SetRect(InfoPanel, colX, y, cover, 54); y += 54 + 18;
        SetRect(ProgressArea, colX, y, cover, 52); y += 52 + 14;
        MoveTo(PrevButton, center - 110, y + 5); MoveTo(PlayButton, center - 30, y); MoveTo(NextButton, center + 60, y + 5);
        y += 60 + 16;
        MoveTo(ModeButton, colX, y); MoveTo(LikeButton, center - 25, y); MoveTo(VolumeButton, colX + cover - 50, y);
        y += 50 + 24;
        MoveTo(LyricsToggle, colX, y); MoveTo(FullScreenToggle, center - 24, y); MoveTo(QueueToggle, colX + cover - 48, y);
    }

    /// <summary>迷你信息头(开面板时):封面缩为 60px 左上角,歌名歌手在其右侧。</summary>
    private void PlaceMiniHeader(double margin, double w)
    {
        Artwork.CornerRadius = new CornerRadius(8);
        SetRect(Artwork, margin, 84, 60, 60);
        SetRect(InfoPanel, margin + 72, 89, w - margin * 2 - 72, 54);
    }

    /// <summary>开面板时的底部切换行:歌词(左) / 全屏(中) / 播放列表(右,音量让位)。</summary>
    private void PlaceBottomToggles(double margin, double w, double h, bool queue)
    {
        var y = h - 62;
        MoveTo(LyricsToggle, margin, y);
        MoveTo(FullScreenToggle, w / 2 - 24, y);
        MoveTo(QueueToggle, w - margin - 48, y);
    }

    private void SetVisibility(bool progress, bool prev, bool play, bool next, bool like, bool mode, bool volume, bool queueToggle)
    {
        ProgressArea.IsVisible = progress;
        PrevButton.IsVisible = prev;
        PlayButton.IsVisible = play;
        NextButton.IsVisible = next;
        LikeButton.IsVisible = like;
        ModeButton.IsVisible = mode;
        VolumeButton.IsVisible = volume;
        QueueToggle.IsVisible = queueToggle;
    }

    // ── 定位与动画(Composition Animation) ─────────────────────────────────

    private void MoveTo(Control control, double x, double y)
    {
        // RenderTransform 保留实际定位/命中测试;Composition Translation 只负责补间动画。
        var old = _positions.TryGetValue(control, out var p) ? p : new Point(x, y);
        control.RenderTransform = Translate(x, y);

        var visual = ElementComposition.GetElementVisual(control);
        if (visual is null)
        {
            _positions[control] = new Point(x, y);
            return;
        }

        visual.StopAnimation("Translation");
        if (_transitionsAttached)
        {
            // 从旧位置到新位置的视觉补间:RenderTransform 已经切到新位置,
            // 所以用 Translation 从 (旧-新) 动画回 0。
            // 先直接把 Translation 设为起点,避免首帧跳到新位置。
            var from = new Vector3D(old.X - x, old.Y - y, 0);
            visual.Translation = from;
            var animation = visual.Compositor.CreateVector3DKeyFrameAnimation();
            animation.Target = "Translation";
            animation.Duration = s_slideDuration;
            animation.InsertKeyFrame(0f, from);
            animation.InsertKeyFrame(1f, default, s_slideEase);
            visual.StartAnimation("Translation", animation);
        }
        else
        {
            visual.Translation = default;
        }

        _positions[control] = new Point(x, y);
    }

    private void SetRect(Control control, double x, double y, double w, double h)
    {
        control.Width = w;
        control.Height = h;
        MoveTo(control, x, y);
    }

    /// <summary>构造 translate(x, y)(避免字符串解析的文化差异/格式问题)。</summary>
    private static TransformOperations Translate(double x, double y)
    {
        var builder = TransformOperations.CreateBuilder(1);
        builder.AppendTranslate(x, y);
        return builder.Build();
    }

    // ── 全屏切换 ────────────────────────────────────────────────────────────

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        ResponsiveClasses.Apply(this, Bounds.Width);
        UpdateDesktopLayout();
        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window is not null)
        {
            // 外部途径(系统快捷键等)切全屏也同步按钮态
            _window.PropertyChanged += OnWindowPropertyChanged;
            SyncFullScreenButton(_window.WindowState);
        }
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (_window is not null) _window.PropertyChanged -= OnWindowPropertyChanged;
        _window = null;
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty && sender is Window window)
            SyncFullScreenButton(window.WindowState);
    }

    private void SyncFullScreenButton(WindowState state)
    {
        var fullScreen = state == WindowState.FullScreen;
        FullScreenToggle.IsChecked = fullScreen;
        FullScreenGlyph.Text = fullScreen ? ExitFullScreenGlyph : EnterFullScreenGlyph;
    }

    private void OnFullScreenClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window window) return;
        window.WindowState = FullScreenToggle.IsChecked == true ? WindowState.FullScreen : WindowState.Normal;
    }

    // ── 播放列表面板 ────────────────────────────────────────────────────────

    /// <summary>队列行容器 realized 时才拉封面(懒加载,与 TrackRow 一致)。</summary>
    private void OnQueueContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container?.DataContext is QueueItemViewModel item)
            item.EnsureCoverLoaded();
    }

    /// <summary>单击队列行播放该曲;排除行内移除按钮的来源。</summary>
    private void OnQueueItemTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual source && source.FindAncestorOfType<Button>() is not null) return;
        if (sender is Control { DataContext: QueueItemViewModel item })
        {
            item.PlayCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>顶部拖拽条:按下并拖动时移动窗口(播放详情页盖住标题栏,靠这里拖)。</summary>
    private void OnTitleDragPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && TopLevel.GetTopLevel(this) is Window w)
            w.BeginMoveDrag(e);
    }

    private void OnTimelinePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.Player.BeginScrub();
    }

    private void OnTimelinePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.Player.EndScrub();
    }
}
