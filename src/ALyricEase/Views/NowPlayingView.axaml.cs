using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Interactivity;
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
  // 全屏按钮图标:进入全屏(E690)/退出全屏(E693),码位见 Icons.axaml
  private const string EnterFullScreenGlyph = "\uE690";
  private const string ExitFullScreenGlyph = "\uE693";

  // 大屏封面下方的控件栈高度:信息54+进度52+控制60+次级50+切换32 + 间距 22+18+14+16+24 = 342
  private const double BelowCoverStack = 342;
  private const double SmallSideMargin = 40;

  private static readonly TimeSpan s_slideDuration = TimeSpan.FromSeconds(0.3);
  private static readonly SplineEasing s_slideEase = new(0.215, 0.61, 0.355, 1); // 与主窗口覆盖层同曲线

  private Window? _window;
  private MainViewModel? _vm;
  private bool _transitionsAttached;
  private bool _progressScrubbing;
  private readonly ProgressRenderAnimator _progressVisual;
  private int _panelPresentationVersion;
  private readonly Dictionary<Control, Point> _positions = new();
  private readonly Dictionary<Control, float> _opacityTargets = new();
  private readonly Dictionary<Control, int> _opacityAnimationVersions = new();
  private readonly Dictionary<Control, int> _sizeAnimationVersions = new();

  // ── 临时诊断:歌词列表"再进去只显示一行"(2026-09-22 加,**排查完连日志一起删**) ──────────
  //
  // 为什么写进应用而不是探针:Headless 探针在同一台机器、1200×720 与 1920×1080 两种尺寸、
  // 五条往返路径上把可疑机制逐条证伪(没重载 / 没丢偏移 / 没回收容器 / 非逐帧实化 /
  // 窗外补间不冻 / 模糊层不晚到),连"保留生产动画负载"那一档也补上了,仍复现不出来。
  // ⇒ 只能拿用户真实运行时的状态:这段埋点把"到底哪一格不对"变成一行可读的量。
  //
  // 开关:Debug 构建默认开;Release 需显式 ALY_LYRIC_DIAG=1;任意构建 ALY_LYRIC_DIAG=0 可关。
  // 输出:%TEMP%\aly-lyric-diag.log(追加写)。**诊断期间应用被强杀也不丢已写的行**(每次重开文件)。
  private static readonly bool s_lyricDiag = IsLyricDiagEnabled();
  private static readonly string s_lyricDiagPath = Path.Combine(Path.GetTempPath(), "aly-lyric-diag.log");
  private int _lyricDiagGeneration;

  private static bool IsLyricDiagEnabled()
  {
    var env = Environment.GetEnvironmentVariable("ALY_LYRIC_DIAG");
    if (env == "0") return false;
    if (env == "1") return true;
#if DEBUG
    return true;
#else
    return false;
#endif
  }

  private static bool s_lyricDiagHeaderWritten;

  private static void DiagWrite(string message)
  {
    if (!s_lyricDiag) return;
    try
    {
      var text = message + Environment.NewLine;
      if (!s_lyricDiagHeaderWritten)
      {
        // 每个进程写一次抬头:**必须能一眼看出这段日志是哪一版跑出来的**。
        // 起因很实际:上一轮排查里疑似跑了旧 DLL(应用开着时编译,ALyricEase.dll 换不掉、
        // 构建却报成功),结果"改完还是老样子"被当成"修法无效"。别靠记忆判版本。
        s_lyricDiagHeaderWritten = true;
        text = $"===== 新进程 PID={Environment.ProcessId} 构建={BuildStamp()} " +
               $"启动={DateTime.Now:yyyy-MM-dd HH:mm:ss} ====={Environment.NewLine}{text}";
      }

      File.AppendAllText(s_lyricDiagPath, text);
    }
    catch
    {
      // 诊断绝不能反过来影响应用
    }
  }

  /// <summary>应用程序集及其构建时间 —— 用来判"这份日志是哪一版跑出来的"。</summary>
  private static string BuildStamp()
  {
    try
    {
      // ⚠ 必须用**应用程序集**,不能用 `Environment.ProcessPath`:后者在 `dotnet run` 下是
      // dotnet.exe,打印出来的是 SDK 的时间戳 —— 那会把"你跑的是哪一版"又骗一次。
      // 而 `ALyricEase.dll` 恰好就是"应用开着时编译换不掉"(MSB3027)的那个文件。
      var path = typeof(NowPlayingView).Assembly.Location;
      return string.IsNullOrEmpty(path)
        ? "?"
        : $"{Path.GetFileName(path)}@{File.GetLastWriteTime(path):MM-dd HH:mm:ss}";
    }
    catch
    {
      return "?";
    }
  }

  /// <summary>开一轮采样:进页面后 0/80/160/240/320/480/800/1500ms 各记一行。</summary>
  private async void RunLyricDiag(string reason)
  {
    if (!s_lyricDiag) return;
    var generation = ++_lyricDiagGeneration;
    try
    {
      var elapsed = 0;
      foreach (var at in new[] { 0, 80, 160, 240, 320, 480, 800, 1500 })
      {
        if (at > elapsed)
        {
          await Task.Delay(at - elapsed);
          elapsed = at;
        }
        if (generation != _lyricDiagGeneration) return; // 又开了一轮 ⇒ 这一轮作废
        DiagWrite($"{Snapshot()} [{reason}] +{elapsed}ms");
      }
    }
    catch
    {
    }
  }

  /// <summary>
  /// 一行里塞进所有能判"哪里不对"的量:窗口尺寸/缩放、面板与列表可见性、歌词数据、选中项、
  /// **实化出来的每个容器**及其高/透明度/模糊半径、以及视口·内容·偏移。
  /// 「实化=1」「容器高度=0」「透明度=0」「模糊缺一档」这些形态在两行之间就能看出来。
  /// </summary>
  private string Snapshot()
  {
    var top = TopLevel.GetTopLevel(this);
    var lyricView = this.GetVisualDescendants().OfType<LyricView>().FirstOrDefault();
    var list = lyricView?.GetVisualDescendants().OfType<ListBox>().FirstOrDefault();
    var scroll = list?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
    var lyricVm = lyricView?.DataContext as LyricViewModel;
    var items = list?.GetVisualDescendants().OfType<ListBoxItem>().ToList();
    // 面板**自己**认的状态:类型、`CacheLength` 读回值、它认定的实化区间。
    // ⚠ 2026-09-23 起那条 `CacheLength=1` 的绕法已删(上游 12.1.2 修掉 auto-sized VSP 的渲染问题)⇒
    // `缓存=0` 是**预期值**,不再代表"样式没生效"。这条读回值仍要留着:它是"面板以为该实化多少"的唯一
    // 直接证据 —— 用户机上出现过 `区间=0..3` 而列表高 897(面板以为只需填一小块 ⇒ 屏幕下半截空白)。
    var vpanel = list?.GetVisualDescendants().OfType<VirtualizingStackPanel>().FirstOrDefault();
    var perItem = items is null
      ? "-"
      : string.Join(" ", items.Select(item =>
      {
        var index = list!.IndexFromContainer(item);
        var effect = item.Effect switch
        {
          null => "-",
          BlurEffect blur => $"b{blur.Radius:F1}",
          IEffect other => other.GetType().Name,
        };
        return $"{index}:{item.Bounds.Height:F0}h/{item.Opacity:F2}/{effect}";
      }));

    return $"{DateTime.Now:HH:mm:ss.fff} " +
           $"win={top?.ClientSize.Width:F0}x{top?.ClientSize.Height:F0}@{top?.RenderScaling:F2} " +
           $"面板={LyricsPanel.IsVisible}/{LyricsPanel.Opacity:F2} 列表可见={list?.IsVisible} " +
           $"行数={lyricVm?.Lines.Count} HasLyric={lyricVm?.HasLyric} 当前句={lyricVm?.CurrentIndex} " +
           $"选中={list?.SelectedIndex} 实化={items?.Count} " +
           $"列表={list?.Bounds.Width:F0}x{list?.Bounds.Height:F0} 视口={scroll?.Viewport.Height:F0} " +
           $"内容={scroll?.Extent.Height:F0} 偏移={scroll?.Offset.Y:F0} " +
           $"面板={vpanel?.GetType().Name}/缓存={vpanel?.CacheLength}/区间={vpanel?.FirstRealizedIndex}..{vpanel?.LastRealizedIndex} " +
           $"容器[{perItem}]";
  }

  private void OnLyricViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
  {
    // 切句也打一行:用户看到的"其余行陆续出现"若跟着切句走,时间线一叠就能看出来。
    if (e.PropertyName is nameof(LyricViewModel.CurrentIndex))
      DiagWrite($"{Snapshot()} [切句]");
  }

  public NowPlayingView()
  {
    InitializeComponent();
    _progressVisual = new ProgressRenderAnimator(ProgressRoot, ProgressTrack, ProgressFill, ProgressThumb);
    // 移动端(触屏 Head)隐藏全屏切换:窗口全屏是桌面概念,移动端整页本来就近乎全屏
    if (InteractionDefaults.IsTouchPrimary) FullScreenToggle.IsVisible = false;
    MoreButton.Click += OnMoreButtonClick;
    SizeChanged += OnSizeChanged;
    AttachedToVisualTree += OnAttachedToVisualTree;
    DetachedFromVisualTree += OnDetachedFromVisualTree;
    DataContextChanged += OnDataContextChanged;
    // Escape 由收起按钮的 HotKeyManager.HotKey 全局处理(XAML),不依赖本视图焦点;
    // 聚焦(OnShowNowPlaying)仅为进度条方向键等内部键盘交互争取焦点,失败不影响 Escape。
  }

  /// <summary>右上角"更多"按钮:弹出当前歌曲菜单(与播放条/歌曲行同一份,见 SongContextMenu)。
  /// 无当前曲目时不弹。</summary>
  private void OnMoreButtonClick(object? sender, RoutedEventArgs e)
  {
    var player = _vm?.Player;
    if (player is null || player.CurrentSong is null) return;
    SongContextMenu.Create(MoreButton, player.CurrentSong, player.QueueSourceName).ShowAt(MoreButton);
  }

  /// <summary>覆盖层常驻(关闭时在屏幕外),打开(ShowNowPlaying=true)时聚焦本视图。</summary>
  private void OnDataContextChanged(object? sender, EventArgs e)
  {
    if (_vm is not null)
    {
      _vm.PropertyChanged -= OnViewModelPropertyChanged;
      _vm.Player.PropertyChanged -= OnPlayerPropertyChanged;
      _vm.Lyric.PropertyChanged -= OnLyricViewModelPropertyChanged;
    }

    _vm = DataContext as MainViewModel;
    if (_vm is not null)
    {
      _vm.PropertyChanged += OnViewModelPropertyChanged;
      _vm.Player.PropertyChanged += OnPlayerPropertyChanged;
      _vm.Lyric.PropertyChanged += OnLyricViewModelPropertyChanged; // 临时诊断用(切句时间线)
      UpdateProgressBar();
    }
    UpdatePanelPresentation(_vm?.NowPlayingPanel ?? NowPlayingPanel.None, animate: false);
    UpdateDesktopLayout();
  }

  private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
  {
    if (sender is not MainViewModel vm) return;
    if (e.PropertyName is nameof(MainViewModel.ShowNowPlaying))
    {
      _progressVisual.SetActive(vm.ShowNowPlaying && vm.Player.HasProgress);
      if (vm.ShowNowPlaying)
      {
        Dispatcher.UIThread.Post(() => Focus(), DispatcherPriority.Background);
        // 进页面时补一次歌词列表的定位与实化自愈:此刻列表面板刚重新可见,
        // 定位请求最容易被"面板还没拿到视口"吃掉(用户机上表现为实化区间塌成 0..0)。
        this.GetVisualDescendants().OfType<LyricView>().FirstOrDefault()?.OnPageShown();
        RunLyricDiag("打开详情页"); // 临时诊断:重进详情页那一刻起的 1.5 秒逐点采样
      }
      else
      {
        // 收起详情页 = 整页平移出窗外(**不是** IsVisible)⇒ 必须显式告诉 LyricView,否则它在离屏期间
        // 照样每条切句做定位补间,而离屏算出来的目标偏移不可信(实测把 Offset 从 792 拖到 4382)
        // ⇒ 用户再进来时得先纠正那个偏移,看起来就是"歌词过一会才加载全"。
        this.GetVisualDescendants().OfType<LyricView>().FirstOrDefault()?.OnPageHidden();
      }
    }
    else if (e.PropertyName is nameof(MainViewModel.NowPlayingPanel))
    {
      UpdateDesktopLayout(); // 面板开关 → 重算布局(控件漂移过去)
      UpdatePanelPresentation(vm.NowPlayingPanel, animate: _transitionsAttached);
      DiagWrite($"{Snapshot()} [面板切到 {vm.NowPlayingPanel}]"); // 临时诊断
    }
  }

  private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
  {
    if (e.PropertyName is nameof(PlayerViewModel.ScrubPositionMs)
        or nameof(PlayerViewModel.DurationMs)
        or nameof(PlayerViewModel.IsPlaying)
        or nameof(PlayerViewModel.HasProgress))
      UpdateProgressBar();
  }

  /// <summary>提交播放器权威进度样本；播放期间由渲染层以 60 FPS 预测推进。</summary>
  private void UpdateProgressBar()
  {
    if (_vm?.Player is not { } player) return;
    _progressVisual.SetActive(_vm.ShowNowPlaying && player.HasProgress);
    _progressVisual.SetPlaybackState(player.ScrubPositionMs, player.DurationMs, player.IsPlaying);
  }

  private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
  {
    ResponsiveClasses.ApplyByWindow(this);
    UpdateDesktopLayout();
  }

  /// <summary>收起按钮:常规顶部居中;中屏无面板时信息区占窗口正上方,故挪到左上(原版行为)。</summary>
  private void PlaceCollapseButton(double w, bool topLeft)
    => MoveTo(CollapseButton, topLeft ? 12 : w / 2 - 25, 32);

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
    // 布局尚未就绪(或 Android 首次测量给极小值)时不排:后续 SizeChanged 会再触发。
    // 若在此处放过极小 w,LayoutSmall 的 rowW = w-84 会算出负数,Width 不接受负值直接抛异常。
    if (w < 60 || h <= 0) return;
    // DataContext 尚未继承到时必须按“无面板”排版；nullable is-not 会把 null 误判为已打开，
    // 导致首帧先落到迷你封面坐标，随后动画从错误起点飞入。
    var panelOpen = _vm is { NowPlayingPanel: not NowPlayingPanel.None };

    // 原版在“很宽但很矮”的窗口中使用独立的横向布局：信息固定在顶部，
    // 封面按高度缩小，但 600px 播放控制列不跟着缩小。此前只按宽度分档，
    // 1750×600 一类窗口会误入紧凑播放列，正是截图中黑色版本的差异来源。
    if (w >= 1200 && h < 720) LayoutShortWide(w, h, panelOpen);
    else if (w >= 1200) LayoutLarge(w, h, panelOpen);
    // 录屏中的 1093px 窗口：无面板仍是顶部信息的中屏布局；打开面板后则保留完整播放列并左右分栏。
    else if (w >= 1000 && panelOpen) LayoutWidePanel(w, h);
    else if (w >= 700) LayoutMedium(w, h, panelOpen);
    else LayoutSmall(w, h, panelOpen);

    // 首次布局完成后再启用 Composition 动画:启动/首开时不应看到从 (0,0) 漂移的动画
    if (!_transitionsAttached)
      Dispatcher.UIThread.Post(() => _transitionsAttached = true, DispatcherPriority.Loaded);
  }

  /// <summary>矮宽中屏（原版约 1750×620 截图基准）。
  /// 信息始终位于顶部中央；封面只随可用高度缩放；进度和两侧按钮保持 600px 宽。
  /// 打开歌词/队列后，播放列整体左移，面板从其右侧开始并吃掉剩余宽度。</summary>
  private void LayoutShortWide(double w, double h, bool panelOpen)
  {
    const double controlWidth = 600;
    const double artworkTop = 126;
    var rowWidth = Math.Min(controlWidth, w - 2 * SmallSideMargin);
    var closedRowX = (w - rowWidth) / 2;
    var openRowX = Math.Clamp(w * 0.06, 72, 104);
    var rowX = panelOpen ? openRowX : closedRowX;
    var center = rowX + rowWidth / 2;

    // 618px 高时约为 194px；窗口降到 584px 时仍保留 160px，且底部按钮完整可见。
    var cover = Math.Clamp(h - 424, 120, Math.Min(300, rowWidth));
    Artwork.CornerRadius = new CornerRadius(16);
    SetRect(Artwork, center - cover / 2, artworkTop, cover, cover);

    var infoWidth = Math.Min(600, w - 160);
    SetRect(InfoPanel, (w - infoWidth) / 2, 36, infoWidth, 54);
    SetInfoCentered(true);
    MoveTo(CollapseButton, 0, 32);

    // 以下 Y 坐标锚定窗口底部，避免 1300×584 时切换按钮落到屏幕外。
    var togglesY = h - 65;
    var secondaryY = togglesY - 66;
    var transportY = secondaryY - 65;
    var progressY = transportY - 58;
    SetRect(ProgressArea, rowX, progressY, rowWidth, 52);
    MoveTo(PrevButton, center - 110, transportY + 5);
    MoveTo(PlayButton, center - 30, transportY);
    MoveTo(NextButton, center + 60, transportY + 5);
    MoveTo(ModeButton, rowX, secondaryY);
    MoveTo(LikeButton, center - 25, secondaryY);
    MoveTo(VolumeButton, rowX + rowWidth - 50, secondaryY);
    MoveTo(LyricsToggle, rowX, togglesY);
    MoveTo(FullScreenToggle, center - 24, togglesY);
    MoveTo(QueueToggle, rowX + rowWidth - 48, togglesY);

    if (panelOpen)
    {
      const double panelGap = 40;
      var panelX = rowX + rowWidth + panelGap;
      var panelRight = Math.Clamp(w * 0.052, 68, 92);
      var panelTop = 84.0;
      var panelW = Math.Max(280, w - panelX - panelRight);
      var panelH = Math.Max(260, h - panelTop - 20);
      SetRect(LyricsPanel, panelX, panelTop, panelW, panelH);
      SetRect(QueuePanel, panelX, panelTop, panelW, panelH);
    }

    SetVisibility(progress: true, prev: true, play: true, next: true, like: true, mode: true, volume: true,
      queueToggle: true);
  }

  /// <summary>大屏(≥1200,1920 截图基准):播放列 = 封面+信息+进度+控制+次级+切换;
  /// 无面板居中;开面板整组左移,面板占剩余宽度(1920 时恰为 898)。</summary>
  private void LayoutLarge(double w, double h, bool panelOpen)
  {
    var margin = 40.0;
    // 宽窗口也可能很矮（例如 1300×584）。原公式已经为顶部标题栏和底部按钮
    // 预留了 114px，但窗口标称高度还可能包含宿主窗口区域，必须再按本视图的
    // 实际 Bounds 钳制最终矩形，不能依赖 MinHeight 推算。
    var preferredCover = Math.Min(h - BelowCoverStack - 114, w - margin * 2);
    var maxCoverToFit = Math.Max(48, h - BelowCoverStack - 24);
    var cover = Math.Clamp(preferredCover, 48, Math.Min(600, maxCoverToFit));
    var colH = cover + BelowCoverStack;
    var preferredTop = (h - colH) / 2 + 35;
    var latestVisibleTop = Math.Max(8, h - colH - 16);
    var colTop = Math.Clamp(preferredTop, 8, latestVisibleTop);
    double colX;
    if (panelOpen)
    {
      // 原版大屏是左右两个等宽区域，而不是“左侧=封面宽、右侧=剩余宽”。
      // 以中心线为轴留出沟槽，两侧使用同一 margin，因此无论窗口多宽都严格 50/50。
      var splitGap = Math.Clamp(w * 0.025, 32, 56);
      var paneW = Math.Max(280, w / 2 - splitGap / 2 - margin);
      var panelX = w / 2 + splitGap / 2;
      colX = margin + (paneW - cover) / 2;
      SetRect(LyricsPanel, panelX, colTop, paneW, colH);
      SetRect(QueuePanel, panelX, colTop, paneW, colH);
    }
    else
    {
      colX = (w - cover) / 2;
    }

    Artwork.CornerRadius = new CornerRadius(16);
    PlaceFullColumn(colX, colTop, cover);
    PlaceCollapseButton(w, topLeft: false);
    SetInfoCentered(false);
    SetVisibility(progress: true, prev: true, play: true, next: true, like: true, mode: true, volume: true,
      queueToggle: true);
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
      SetRect(ProgressArea, colX, y, cover, 52);
      y += 52 + 14;
      MoveTo(PrevButton, center - 110, y + 5);
      MoveTo(PlayButton, center - 30, y);
      MoveTo(NextButton, center + 60, y + 5);
      y += 60 + 16;
      MoveTo(ModeButton, colX, y);
      MoveTo(LikeButton, center - 25, y);
      MoveTo(VolumeButton, colX + cover - 50, y);
      y += 50 + 24;
      MoveTo(LyricsToggle, colX, y);
      MoveTo(FullScreenToggle, center - 24, y);
      MoveTo(QueueToggle, colX + cover - 48, y);
      SetVisibility(progress: true, prev: true, play: true, next: true, like: true, mode: true, volume: true,
        queueToggle: true);
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
      SetVisibility(progress: false, prev: false, play: false, next: false, like: false, mode: false, volume: false,
        queueToggle: true);
    }
  }

  /// <summary>宽中屏(录屏 1093×1058)打开歌词/队列后的原版分栏：
  /// 左侧播放列缩窄但保留全部控制，右侧面板占据剩余空间。</summary>
  private void LayoutWidePanel(double w, double h)
  {
    var playerX = Math.Clamp(w * 0.025, 24, 40);
    var panelGap = Math.Clamp(w * 0.04, 36, 50);
    // 与大屏一致，以窗口中心线切出两个严格等宽的可用区域。
    var playerW = Math.Max(320, w / 2 - panelGap / 2 - playerX);
    var panelX = w / 2 + panelGap / 2;
    var panelW = playerW;

    // 原版宽中屏的纵向节奏与窄屏播放列一致，所有控制锚定底部；
    // 封面在播放列内居中，并为歌名区预留约 90px 的呼吸空间。
    var togglesY = h - 62;
    var secondaryY = togglesY - 94;
    var transportY = secondaryY - 92;
    var progressY = transportY - 86;
    var infoY = progressY - 82;
    var coverTop = Math.Clamp(h * 0.17, 130, 180);
    var availableCover = Math.Max(260, infoY - coverTop - 94);
    var cover = Math.Clamp(Math.Min(playerW * 0.75, availableCover), 260, 380);
    var center = playerX + playerW / 2;

    Artwork.CornerRadius = new CornerRadius(16);
    SetRect(Artwork, center - cover / 2, coverTop, cover, cover);
    SetRect(InfoPanel, playerX, infoY, playerW, 54);
    SetRect(ProgressArea, playerX, progressY, playerW, 52);
    MoveTo(PrevButton, center - 110, transportY + 5);
    MoveTo(PlayButton, center - 30, transportY);
    MoveTo(NextButton, center + 60, transportY + 5);
    MoveTo(ModeButton, playerX, secondaryY);
    MoveTo(LikeButton, center - 25, secondaryY);
    MoveTo(VolumeButton, playerX + playerW - 50, secondaryY);
    MoveTo(LyricsToggle, playerX, togglesY);
    MoveTo(FullScreenToggle, center - 24, togglesY);
    MoveTo(QueueToggle, playerX + playerW - 48, togglesY);

    var panelTop = Math.Clamp(h * 0.095, 82, 106);
    var panelH = Math.Max(260, h - panelTop - 20);
    SetRect(LyricsPanel, panelX, panelTop, panelW, panelH);
    SetRect(QueuePanel, panelX, panelTop, panelW, panelH);

    PlaceCollapseButton(w, topLeft: false);
    SetInfoCentered(false);
    SetVisibility(progress: true, prev: true, play: true, next: true, like: true, mode: true, volume: true,
      queueToggle: true);
  }

  /// <summary>小屏(&lt;700,502x954 截图基准)。无面板:与大屏相同的完整控件栈,但各行横跨整列(边距 42),
  /// 封面居中收窄;控件栈锚定底部,封面锚定顶部(0.17h),中间空隙留给封面→信息区。
  /// 开面板:左上迷你头 + 面板撑满 + 底部行(歌词/全屏/播放列表)。</summary>
  private void LayoutSmall(double w, double h, bool panelOpen)
  {
    if (!panelOpen)
    {
      // 500×1000 原版实测基准:左右各 40、420px 正方形封面、封面顶 120；
      // 下方各行不是等距紧凑栈，而是 584/666/752/844/938 的节奏。
      var rowX = SmallSideMargin;
      var rowW = w - SmallSideMargin * 2;
      var togglesY = h - 62;
      var secondaryY = togglesY - 94;
      var transportY = secondaryY - 92;
      var progressY = transportY - 86;
      var infoY = progressY - 82;
      var coverTop = Math.Clamp(h * 0.12, 86, 120);
      var availableCover = Math.Max(160, infoY - coverTop - 44);
      var cover = Math.Clamp(Math.Min(rowW, availableCover), 160, 600);
      Artwork.CornerRadius = new CornerRadius(16);
      SetRect(Artwork, rowX + (rowW - cover) / 2, coverTop, cover, cover);
      var center = rowX + rowW / 2;
      SetRect(InfoPanel, rowX, infoY, rowW, 54);
      SetRect(ProgressArea, rowX, progressY, rowW, 52);
      MoveTo(PrevButton, center - 110, transportY + 5);
      MoveTo(PlayButton, center - 30, transportY);
      MoveTo(NextButton, center + 60, transportY + 5);
      MoveTo(ModeButton, rowX, secondaryY);
      MoveTo(LikeButton, center - 25, secondaryY);
      MoveTo(VolumeButton, rowX + rowW - 50, secondaryY);
      MoveTo(LyricsToggle, rowX, togglesY);
      MoveTo(FullScreenToggle, center - 24, togglesY);
      MoveTo(QueueToggle, rowX + rowW - 48, togglesY);
      PlaceCollapseButton(w, topLeft: false);
      SetInfoCentered(false);
      SetVisibility(progress: true, prev: true, play: true, next: true, like: true, mode: true, volume: true,
        queueToggle: true);
    }
    else
    {
      PlaceMiniHeader(42, w);
      PlaceCollapseButton(w, topLeft: false);
      SetInfoCentered(false);
      var panelTop = 160.0;
      SetRect(LyricsPanel, 33, panelTop, w - 66, h - panelTop - 70);
      SetRect(QueuePanel, 33, panelTop, w - 66, h - panelTop - 70);
      // 底部三个切换按钮在开关面板前后必须保持同一坐标；否则 40→42 的
      // 两像素差也会被 MoveTo 补间，看起来像按下后先外移再弹回来。
      PlaceBottomToggles(SmallSideMargin, w, h, queue: true);
      SetVisibility(progress: false, prev: false, play: false, next: false, like: false, mode: false, volume: false,
        queueToggle: true);
    }
  }

  /// <summary>大屏的完整播放列(封面 + 封面下方控件栈,各行与封面同宽)。</summary>
  private void PlaceFullColumn(double colX, double colTop, double cover)
  {
    var center = colX + cover / 2;
    SetRect(Artwork, colX, colTop, cover, cover);
    var y = colTop + cover + 22;
    SetRect(InfoPanel, colX, y, cover, 54);
    y += 54 + 18;
    SetRect(ProgressArea, colX, y, cover, 52);
    y += 52 + 14;
    MoveTo(PrevButton, center - 110, y + 5);
    MoveTo(PlayButton, center - 30, y);
    MoveTo(NextButton, center + 60, y + 5);
    y += 60 + 16;
    MoveTo(ModeButton, colX, y);
    MoveTo(LikeButton, center - 25, y);
    MoveTo(VolumeButton, colX + cover - 50, y);
    y += 50 + 24;
    MoveTo(LyricsToggle, colX, y);
    MoveTo(FullScreenToggle, center - 24, y);
    MoveTo(QueueToggle, colX + cover - 48, y);
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

  private void SetVisibility(bool progress, bool prev, bool play, bool next, bool like, bool mode, bool volume,
    bool queueToggle)
  {
    SetLayoutControlVisible(ProgressArea, progress);
    SetLayoutControlVisible(PrevButton, prev);
    SetLayoutControlVisible(PlayButton, play);
    SetLayoutControlVisible(NextButton, next);
    SetLayoutControlVisible(LikeButton, like);
    SetLayoutControlVisible(ModeButton, mode);
    SetLayoutControlVisible(VolumeButton, volume);
    QueueToggle.IsVisible = queueToggle;
  }

  private void SetLayoutControlVisible(Control control, bool visible)
  {
    // 不切 IsVisible，避免退出动画被立即裁掉；透明控件同时关闭命中测试。
    control.IsHitTestVisible = visible;
    AnimateOpacity(control, visible ? 1f : 0f);
  }

  /// <summary>歌词/队列面板使用常驻画布 + 300ms 交叉淡入淡出。
  /// 退出面板延迟到动画完成后再折叠，快速连点时用版本号避免旧回调误隐藏新面板。</summary>
  private void UpdatePanelPresentation(NowPlayingPanel panel, bool animate)
  {
    var version = ++_panelPresentationVersion;
    SetPanelVisible(LyricsPanel, panel == NowPlayingPanel.Lyrics, version, animate);
    SetPanelVisible(QueuePanel, panel == NowPlayingPanel.Queue, version, animate);
  }

  private void SetPanelVisible(Control control, bool visible, int version, bool animate)
  {
    if (visible)
    {
      control.IsVisible = true;
      control.IsHitTestVisible = true;
      AnimateOpacity(control, 1f, animate);
      AnimatePanelSlide(GetPanelContent(control), entering: true, animate);
      return;
    }

    control.IsHitTestVisible = false;
    AnimateOpacity(control, 0f, animate);
    AnimatePanelSlide(GetPanelContent(control), entering: false, animate);
    if (!animate)
    {
      control.IsVisible = false;
      return;
    }

    DispatcherTimer.RunOnce(() =>
    {
      if (version == _panelPresentationVersion
          && _opacityTargets.GetValueOrDefault(control) <= 0)
        control.IsVisible = false;
    }, s_slideDuration);
  }

  // ── 定位与动画(Composition Animation) ─────────────────────────────────

  private void MoveTo(Control control, double x, double y)
  {
    // RenderTransform 保留实际定位/命中测试;Composition Translation 只负责补间动画。
    var old = _positions.TryGetValue(control, out var p) ? p : new Point(x, y);
    control.RenderTransform = Translate(x, y);

    // IsChecked/IsVisible 改变可能在同一轮布局中再次触发排版。目标坐标没变时
    // 不要停止并重建 Translation 动画，否则按钮会在中途被吸回终点，形成回弹感。
    if (Math.Abs(old.X - x) < 0.01 && Math.Abs(old.Y - y) < 0.01)
    {
      _positions[control] = new Point(x, y);
      return;
    }

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

  private Control GetPanelContent(Control panel)
    => panel == LyricsPanel ? LyricsPanelContent : QueuePanelContent;

  /// <summary>面板内容在自己的合成层做短距离纵向位移。外层面板仍由 MoveTo 定位，
  /// 因而进退场动画不会覆盖响应式布局坐标，也不会把命中区域留在旧位置。</summary>
  private void AnimatePanelSlide(Control content, bool entering, bool animate)
  {
    var visual = ElementComposition.GetElementVisual(content);
    if (visual is null) return;

    visual.StopAnimation("Translation");
    var offset = new Vector3D(0, Math.Clamp(Bounds.Height * 0.065, 40, 68), 0);
    var target = entering ? default : offset;
    visual.Translation = target;
    if (!_transitionsAttached || !animate) return;

    var animation = visual.Compositor.CreateVector3DKeyFrameAnimation();
    animation.Target = "Translation";
    animation.Duration = s_slideDuration;
    animation.InsertKeyFrame(0f, entering ? offset : default);
    animation.InsertKeyFrame(1f, target, s_slideEase);
    visual.StartAnimation("Translation", animation);
  }

  /// <summary>直接补间 Avalonia 的真实 Opacity 属性，动画结束时明确落到 0/1。
  /// 这可以避免控件重排或视觉层重建后，旧的合成层透明度丢失而重新露出进度条。</summary>
  private void AnimateOpacity(Control control, float target, bool animate = true)
  {
    var from = control.Opacity;
    _opacityTargets[control] = target;
    var version = _opacityAnimationVersions.GetValueOrDefault(control) + 1;
    _opacityAnimationVersions[control] = version;

    if (!_transitionsAttached || !animate || Math.Abs(from - target) < 0.001)
    {
      control.Opacity = target;
      return;
    }

    if (TopLevel.GetTopLevel(this) is not { } topLevel)
    {
      control.Opacity = target;
      return;
    }

    TimeSpan? startedAt = null;
    Action<TimeSpan>? tick = null;
    tick = now =>
    {
      if (_opacityAnimationVersions.GetValueOrDefault(control) != version) return;
      startedAt ??= now;
      var progress = Math.Clamp((now - startedAt.Value).TotalMilliseconds / s_slideDuration.TotalMilliseconds, 0, 1);
      var eased = EaseOutCubic(progress);
      control.Opacity = from + (target - from) * eased;
      if (progress < 1)
        topLevel.RequestAnimationFrame(tick!);
      else
        control.Opacity = target;
    };
    topLevel.RequestAnimationFrame(tick);
  }

  /// <summary>封面尺寸通过布局属性独立补间。不要再用 Composition Scale：Scale 会连同
  /// Translation 一起变换，封面缩小时会把位移放大，视觉上就会从右下角飞向左上角。</summary>
  private void SetArtworkSize(Size target)
  {
    var from = new Size(
      double.IsNaN(Artwork.Width) ? Artwork.Bounds.Width : Artwork.Width,
      double.IsNaN(Artwork.Height) ? Artwork.Bounds.Height : Artwork.Height);
    var version = _sizeAnimationVersions.GetValueOrDefault(Artwork) + 1;
    _sizeAnimationVersions[Artwork] = version;

    if (!_transitionsAttached || from.Width <= 0 || from.Height <= 0
        || (Math.Abs(from.Width - target.Width) < 0.5 && Math.Abs(from.Height - target.Height) < 0.5)
        || TopLevel.GetTopLevel(this) is not { } topLevel)
    {
      Artwork.Width = target.Width;
      Artwork.Height = target.Height;
      return;
    }

    TimeSpan? startedAt = null;
    Action<TimeSpan>? tick = null;
    tick = now =>
    {
      if (_sizeAnimationVersions.GetValueOrDefault(Artwork) != version) return;
      startedAt ??= now;
      var progress = Math.Clamp((now - startedAt.Value).TotalMilliseconds / s_slideDuration.TotalMilliseconds, 0, 1);
      var eased = EaseOutCubic(progress);
      Artwork.Width = from.Width + (target.Width - from.Width) * eased;
      Artwork.Height = from.Height + (target.Height - from.Height) * eased;
      if (progress < 1)
        topLevel.RequestAnimationFrame(tick!);
      else
      {
        Artwork.Width = target.Width;
        Artwork.Height = target.Height;
      }
    };
    topLevel.RequestAnimationFrame(tick);
  }

  private static double EaseOutCubic(double progress) => 1 - Math.Pow(1 - progress, 3);

  private void SetRect(Control control, double x, double y, double w, double h)
  {
    // 防御:布局计算可能算出负尺寸(极小可用宽),Avalonia 的 Width/Height 不接受负值(抛 ArgumentException)
    var newSize = new Size(Math.Max(0, w), Math.Max(0, h));
    if (control == Artwork)
      SetArtworkSize(newSize);
    else
    {
      control.Width = newSize.Width;
      control.Height = newSize.Height;
    }
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
    _progressVisual.Attach();
    ResponsiveClasses.ApplyByWindow(this);
    UpdateDesktopLayout();
    UpdateProgressBar();
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
    _progressVisual.Detach();
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

  private void OnLyricSeekRequested(object? sender, LyricSeekRequestedEventArgs e)
    => _vm?.Player.SeekTo(e.PositionMs);

  /// <summary>顶部拖拽条:按下并拖动时移动窗口(播放详情页盖住标题栏,靠这里拖)。</summary>
  private void OnTitleDragPressed(object? sender, PointerPressedEventArgs e)
  {
    if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
    if (TopLevel.GetTopLevel(this) is not Window w) return;
    if (w.WindowState == WindowState.FullScreen) return; // 全屏下禁止拖动窗口
    w.BeginMoveDrag(e);
  }

  private void OnProgressPointerPressed(object? sender, PointerPressedEventArgs e)
  {
    if (_vm?.Player is not { } player) return;
    if (!e.GetCurrentPoint(ProgressRoot).Properties.IsLeftButtonPressed) return;

    _progressScrubbing = true;
    _progressVisual.SetScrubbing(true);
    e.Pointer.Capture(ProgressRoot);
    player.BeginScrub();
    SetProgressFromPointer(e);
    e.Handled = true;
  }

  private void OnProgressPointerMoved(object? sender, PointerEventArgs e)
  {
    if (_progressScrubbing) SetProgressFromPointer(e);
  }

  private void OnProgressPointerReleased(object? sender, PointerReleasedEventArgs e)
  {
    if (!_progressScrubbing) return;
    _progressScrubbing = false;
    e.Pointer.Capture(null);
    if (_vm?.Player is { } player) player.EndScrub();
    _progressVisual.SetScrubbing(false);
    e.Handled = true;
  }

  private void SetProgressFromPointer(PointerEventArgs e)
  {
    if (_vm?.Player is not { } player) return;
    var width = ProgressTrack.Bounds.Width;
    if (width <= 0 || player.DurationMs <= 0) return;

    var ratio = Math.Clamp(e.GetPosition(ProgressTrack).X / width, 0, 1);
    var positionMs = player.DurationMs * ratio;
    player.ScrubPositionMs = positionMs;
    _progressVisual.SetScrubPosition(positionMs, player.DurationMs);
  }
}
