using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ALyricEase.Models;
using ALyricEase.ViewModels;

namespace ALyricEase.Views;

/// <summary>歌词视图:当前句变化时把选中项居中滚动(SelectionChanged 驱动)。
/// 先用 ScrollIntoView 物化容器，再按 ScrollViewer.Offset 做逐帧平滑居中。
/// detail 模式(播放详情页歌词面板)映射原版 AbovePresent/BelowPresent 状态，
/// 以方向性透明度、模糊和缩放区分已唱/当前/待唱歌词。</summary>
public partial class LyricView : UserControl
{
    private int _scrollAnimationVersion;

    /// <summary>
    /// 手动滚动(滚轮/滚动条拖拽/触摸平移)后自动跟随的暂停时长:暂停期只高亮不拉回,
    /// 到点后的下一句变化恢复跟随。修"滚到一半被下一句拉回去"(2026-09-23)。
    /// ⚠ 依赖 XAML 侧 <c>AutoScrollToSelectedItem="False"</c>:默认 true 时 ListBox 会在
    /// 选中变化时自己 ScrollIntoView,这条抑制拦不住框架的拉回(--lyrscroll 实测复现,2026-09-30 修)。
    /// </summary>
    private const long AutoFollowPauseMs = 4000;
    private long _lastUserScrollTimestamp;

    /// <summary>
    /// 自身 + **全部祖先**的 `IsVisible` 订阅集(必须记住:脱离视觉树后祖先链就断了,靠回走收不回来,
    /// 而那些祖先比本控件活得久,漏解就是真泄漏)。
    ///
    /// <para>
    /// 为什么不直接读 <c>IsEffectivelyVisible</c>:Avalonia 12 里它**只有 getter**、没有可订阅的属性
    /// (`IndeterminateAnimationGate` 也踩过同一件事)⇒ 只能把整条链订阅下来,任一层变化都当作
    /// "有效可见性可能变了"。判定见 <see cref="IsEffectivelyPresentable"/>。
    /// </para>
    /// </summary>
    private readonly List<Visual> _visibilityChain = [];
    private EventHandler<AvaloniaPropertyChangedEventArgs>? _visibilityHandler;

    /// <summary>
    /// 不可见期间攒下的定位请求(见 <see cref="OnSelectionChanged"/>):记"要把哪一项滚到中间",
    /// `-1` = 无事可做。重新可见时由 <see cref="OnVisibilityChainChanged"/> 补做一次。
    /// </summary>
    private int _pendingRecenterIndex = -1;

    /// <summary>
    /// 详情页是不是被**平移出窗外**(收起状态)。
    ///
    /// <para>
    /// ⚠ 这才是本控件真正的"看不见":页面收起时 `IsVisible` 全程仍是 true
    /// (`NowPlayingOverlayController` 只用 `RenderTransform` 把整页移出去),所以光订阅 `IsVisible` 抓不到。
    /// 而页面收起期间**每条切句都会走到这里**:离屏状态下 `TranslatePoint` 算不出可信的容器位置,
    /// 定位补间会把 `Offset` 拖到离谱的地方 —— 实测 **792 → 4382**(当前句才第 12 行),
    /// 用户进屋后必须先纠正这个被拖跑的偏移(模型塌成 0..0、再从 0 号逐页实化回来,约 0.5 秒),
    /// 那正是"进详情页时歌词要过一会才加载全"。
    /// </para>
    /// <para>
    /// 初值是 <c>true</c>:控件刚构造时页面还没打开过,不该先跑定位。
    /// </para>
    /// </summary>
    private bool _pageParked = true;

    /// <summary>用户点按一行歌词后，请求播放器跳到该行在实际播放时间轴上的位置。</summary>
    public event EventHandler<LyricSeekRequestedEventArgs>? SeekRequested;

    /// <summary>歌词字号缩放系数(详情页字号档位 60%~150%,1 = 100%)。</summary>
    public static readonly StyledProperty<double> FontScaleProperty =
        AvaloniaProperty.Register<LyricView, double>(nameof(FontScale), 1.0);

    /// <summary>
    /// 歌词行要不要模糊。true(默认)= 样式里的正常样子:近处 1~2.5px、远景 5px。
    /// false = 整个面板不挂 <c>Effect</c>,只留透明度分级 ——
    /// 挂 Effect 的行都要走"离屏层 + 图像过滤"这条管线,整档关掉实测省 ≈0.95% GPU(1200×720,≈0.24%/行)。
    /// 由设置「性能与体验」驱动(最佳质量 = true,最佳性能 = false),见 <c>MainViewModel.LyricBlurEnabled</c>。
    /// </summary>
    public static readonly StyledProperty<bool> BlurEnabledProperty =
        AvaloniaProperty.Register<LyricView, bool>(nameof(BlurEnabled), defaultValue: true);

    public double FontScale
    {
        get => GetValue(FontScaleProperty);
        set => SetValue(FontScaleProperty, value);
    }

    public bool BlurEnabled
    {
        get => GetValue(BlurEnabledProperty);
        set => SetValue(BlurEnabledProperty, value);
    }

    /// <summary>
    /// 关模糊**只改类**,不写任何行的 <c>Effect</c> 本地值:类变了一样会触发样式重算,
    /// 而本地值会盖过样式 ⇒ 以后样式改半径、改哪几行带模糊,这里就全都不跟了。
    /// </summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BlurEnabledProperty)
            Classes.Set("no-blur", !BlurEnabled);
    }

    public LyricView()
    {
        InitializeComponent();
        // 虚拟化回收容器后类会丢,每次 realized 按当前距离重设
        LyricList.ContainerPrepared += OnContainerPrepared;
        // 鼠标按下时 ListBoxItem 会先改选中项并触发居中滚动，导致松开时目标已移位、Tap 被取消。
        // 与侧栏列表保持一致：鼠标选择交给 Tap 完成；触摸/笔仍保留原生按压以支持滚动手势。
        LyricList.AddHandler(
            InputElement.PointerPressedEvent,
            OnLyricListPointerPressed,
            RoutingStrategies.Tunnel);
        // ListBoxItem 会把 Tap 标记为已处理，仍需在同一次 Tap 中执行跳转。
        LyricList.AddHandler(
            InputElement.TappedEvent,
            OnLyricListTapped,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        // 用户手动滚动打标(滚轮/按下/按住拖拽),暂停期内自动跟随让位(见 AutoFollowPauseMs)。
        LyricList.AddHandler(
            InputElement.PointerWheelChangedEvent,
            OnLyricListUserScroll,
            RoutingStrategies.Tunnel);
        LyricList.AddHandler(
            InputElement.PointerPressedEvent,
            OnLyricListUserScroll,
            RoutingStrategies.Tunnel);
        LyricList.AddHandler(
            InputElement.PointerMovedEvent,
            OnLyricListPointerMoved,
            RoutingStrategies.Tunnel);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        SubscribeVisibilityChain();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        UnsubscribeVisibilityChain();
        base.OnDetachedFromVisualTree(e);
    }

    private void SubscribeVisibilityChain()
    {
        _visibilityHandler ??= OnVisibilityChainChanged;
        if (_visibilityChain.Count > 0) return;
        for (Visual? node = this; node is not null; node = node.GetVisualParent())
        {
            node.PropertyChanged += _visibilityHandler;
            _visibilityChain.Add(node);
        }
    }

    private void UnsubscribeVisibilityChain()
    {
        if (_visibilityHandler is null) return;
        foreach (var node in _visibilityChain) node.PropertyChanged -= _visibilityHandler;
        _visibilityChain.Clear();
    }

    /// <summary>重新变得可见时,把不可见期间攒下的定位请求补做一次。</summary>
    private void OnVisibilityChainChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Visual.IsVisibleProperty) return;
        if (_pendingRecenterIndex < 0 || !IsEffectivelyPresentable()) return;

        var index = _pendingRecenterIndex;
        _pendingRecenterIndex = -1;
        Dispatcher.UIThread.Post(() => Recenter(index), DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 现在能不能做定位/补间(等价 <c>IsEffectivelyVisible</c>,另加"页面没被平移出窗外")。
    /// ⚠ 前半段只能自己算:Avalonia 12 的 `IsEffectivelyVisible` **只有 getter**、不可订阅,
    /// 所以退化成"自身 + 全部祖先的 `IsVisible`";后半段靠 <see cref="OnPageShown"/> /
    /// <see cref="OnPageHidden"/> 显式驱动,因为平移出窗外不改任何 `IsVisible`。
    /// </summary>
    private bool IsEffectivelyPresentable()
    {
        if (_pageParked) return false;
        for (Visual? node = this; node is not null; node = node.GetVisualParent())
            if (!node.IsVisible) return false;
        return true;
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateProgressClasses();

        var index = LyricList.SelectedIndex;
        if (index < 0) return;

        // ⚠ **不可见时不要发定位请求。** `ScrollIntoView` 里有 `if (!IsEffectivelyVisible) return null`
        // —— 请求会被静默丢弃,而"隐藏期间"的这场定位正是游离容器的来源(上游 #15194,详见
        // docs/avalonia-tips.md 末节)。改成记账,等重新可见时补一次;在途补间也一起掐掉,
        // 否则它会继续往一个不可见的偏移上逐帧写。
        if (!IsEffectivelyPresentable())
        {
            _pendingRecenterIndex = index;
            _scrollAnimationVersion++;
            return;
        }

        // 手动滚动暂停期:只高亮不拉回(见 AutoFollowPauseMs),version++ 顺带掐掉在途补间。
        if (IsAutoFollowSuppressed)
        {
            _scrollAnimationVersion++;
            return;
        }

        Recenter(index);
    }

    private bool IsAutoFollowSuppressed
        => Environment.TickCount64 - _lastUserScrollTimestamp < AutoFollowPauseMs;

    private void OnLyricListUserScroll(object? sender, RoutedEventArgs e)
        => MarkUserScroll();

    /// <summary>按住拖动(滚动条拖拽/触摸平移)算用户滚动;悬停移动不算。</summary>
    private void OnLyricListPointerMoved(object? sender, PointerEventArgs e)
    {
        if (e.GetCurrentPoint(LyricList).Properties.IsLeftButtonPressed)
            MarkUserScroll();
    }

    /// <summary>打标并掐掉在途补间 —— 否则补间逐帧写绝对 Offset 会覆盖用户的滚动位移。</summary>
    private void MarkUserScroll()
    {
        _lastUserScrollTimestamp = Environment.TickCount64;
        _scrollAnimationVersion++;
        SuspendBlurForUserScroll();
    }

    // ── 手动滚动期的模糊渐隐/渐显(2026-10-01)─────────────────────────────
    //
    // 找歌词时逐行模糊碍事 ⇒ 手动滚动期间把模糊"慢慢"取消,自动跟随**真正拉回**时再"慢慢"模糊回去。
    // 机制:detail 的四个模糊档位是 UserControl.Resources 里的**共享** BlurEffect
    // (样式 Setter 引用同一实例,见 LyricView.axaml 资源区注释)——只改它们的 Radius,
    // 所有挂着的行一起渐变,Transitions(0.35s)负责动画。
    //
    // 两阶段摘除:① Radius 渐变到 0;② 350ms 后挂 blur-suspended 类把 Effect 真正置 null
    // (半径 0 仍走「离屏层 + 图像过滤」管线,不置 null 就是白烧 GPU)。
    // 恢复时机:**不在固定 4 秒** —— 4 秒只是自动跟随解除抑制的门槛,到点后若还没切句,
    // 列表本来就没动,模糊保持摘除;等下一次切句真正拉回(Recenter)才把模糊渐变回去。
    // _blurGeneration 防"滚动前安排的渐隐定时器把已恢复的状态又摘掉":每次滚动 +1,定时器校验。

    private const double BlurRadiusFar = 5;
    private const double BlurRadiusAbove = 1.5;
    private const double BlurRadiusBelow1 = 1;
    private const double BlurRadiusBelow2 = 2.5;
    private static readonly TimeSpan BlurFadeDuration = TimeSpan.FromMilliseconds(350);

    private BlurEffect? _blurFar;
    private BlurEffect? _blurAbove;
    private BlurEffect? _blurBelow1;
    private BlurEffect? _blurBelow2;
    private int _blurGeneration;
    private bool _blurSuspended;

    /// <summary>手动滚动开始:模糊渐隐(性能档/非 detail 本来就没模糊,无事发生)。</summary>
    private void SuspendBlurForUserScroll()
    {
        if (!BlurEnabled || !Classes.Contains("detail")) return;
        EnsureBlurResources();
        if (_blurFar is null || _blurAbove is null || _blurBelow1 is null || _blurBelow2 is null) return;

        _blurSuspended = true;
        var gen = ++_blurGeneration;
        SetSharedBlurRadius(0);

        // 渐变完成后整档摘 Effect(半径 0 ≠ 免费,见类注释)。generation 校验:期间又滚动则新轮接管。
        DispatcherTimer.RunOnce(
            () =>
            {
                if (gen != _blurGeneration) return;
                Classes.Set("blur-suspended", true);
            },
            BlurFadeDuration + TimeSpan.FromMilliseconds(50));

        // 恢复不在固定 4 秒:由 Recenter(自动跟随真正拉回)触发,见 ResumeBlurFromUserScroll。
    }

    /// <summary>自动跟随拉回(Recenter)时把模糊渐变回去:先摘 blur-suspended
    /// (共享实例以 Radius=0 重新挂上),再把半径渐回档位值 ⇒ 渐显而非跳变。</summary>
    private void ResumeBlurFromUserScroll()
    {
        _blurSuspended = false;
        Classes.Set("blur-suspended", false);
        if (!BlurEnabled || !Classes.Contains("detail")) return;
        EnsureBlurResources();
        if (_blurFar is null || _blurAbove is null || _blurBelow1 is null || _blurBelow2 is null) return;

        SetSharedBlurRadius(null);
    }

    /// <summary>factor=null = 恢复各档位标称值;0 = 全部渐隐到无。</summary>
    private void SetSharedBlurRadius(double? factor)
    {
        if (_blurFar is not null) _blurFar.Radius = factor is null ? BlurRadiusFar : factor.Value;
        if (_blurAbove is not null) _blurAbove.Radius = factor is null ? BlurRadiusAbove : factor.Value;
        if (_blurBelow1 is not null) _blurBelow1.Radius = factor is null ? BlurRadiusBelow1 : factor.Value;
        if (_blurBelow2 is not null) _blurBelow2.Radius = factor is null ? BlurRadiusBelow2 : factor.Value;
    }

    private void EnsureBlurResources()
    {
        if (_blurFar is not null) return;
        _blurFar = Resources["LyricBlurFar"] as BlurEffect;
        _blurAbove = Resources["LyricBlurAbove"] as BlurEffect;
        _blurBelow1 = Resources["LyricBlurBelow1"] as BlurEffect;
        _blurBelow2 = Resources["LyricBlurBelow2"] as BlurEffect;
    }


    /// <summary>把目标行带入视口,再在下次布局后做居中补间(仅在有效可见时调用)。
    /// 也是手动滚动模糊的恢复点:只有这里代表"列表真的被拉回去跟歌词了"。</summary>
    private void Recenter(int index)
    {
        if (_blurSuspended) ResumeBlurFromUserScroll();
        LyricList.ScrollIntoView(index);
        var version = ++_scrollAnimationVersion;
        Dispatcher.UIThread.Post(() => AnimateSelectedToCenter(index, version), DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 详情页重新出现时调一次:此时列表的定位请求最容易被"面板还没拿到视口"吃掉。
    /// 连发两拍(当帧 + 隔一帧),让定位在布局之后一定重来一次。
    /// </summary>
    internal void OnPageShown()
    {
        // 页面收起时是"平移出窗外",`IsVisible` 不变 ⇒ 可见性订阅链**不会**被触发,
        // 所以解封与补做都得在这里显式做。
        _pageParked = false;

        // 不可见期间攒下的定位请求在这里补做一次。只有真攒过才补:
        // 健康路径上多叫一次 `ScrollIntoView` 本身就是在给上游那个锚点缺陷递机会。
        var pending = _pendingRecenterIndex;
        _pendingRecenterIndex = -1;
        if (pending >= 0 && IsEffectivelyPresentable())
            Dispatcher.UIThread.Post(() => Recenter(pending), DispatcherPriority.Loaded);

        // 再补一拍居中:健康时是空操作(目标位移 ≤0.5px 就返回),只在"进屋时布局还没落定、
        // 上一拍的补间没算准"时才有实际作用。
        Dispatcher.UIThread.Post(
            () =>
            {
                var index = LyricList.SelectedIndex;
                if (index >= 0) AnimateSelectedToCenter(index, _scrollAnimationVersion);
            },
            DispatcherPriority.Background);
    }

    /// <summary>
    /// 详情页被收起(整页平移出窗外)时调一次:此后不再做定位与补间。
    ///
    /// <para>
    /// 离屏时容器位置的 `TranslatePoint` 不可信 ⇒ 补间会把 `Offset` 拖到离谱的地方
    /// (实测 792 → 4382,当前句才第 12 行),而且那段补间没人看得见。收起期间一律只记账,
    /// 由 <see cref="OnPageShown"/> 一次补上。⚠ 顺带掐掉在途补间 —— 否则它会继续往那个离屏偏移上逐帧写。
    /// </para>
    /// </summary>
    internal void OnPageHidden()
    {
        _pageParked = true;
        _scrollAnimationVersion++;
    }

    // 这里曾有 `HealRealization` / `HealReporter`(实化自愈):检测到面板模型塌成 `0..0`、或视觉树里
    // 多出一个 `IndexFromContainer == -1` 的游离容器时,就掐补间 → `InvalidateMeasure` → 仍不行则重挂集合。
    // **已删除**(2026-09-23):那两条病理都属于 `VirtualizingStackPanel` 的实化模型,而上游 12.1.2 已修掉
    // auto-sized 面板的渲染问题(#22081)。⚠ 若"只实化一两行 / 同一句重复"复发,先从
    // `artifacts/lyric-workarounds-before-removal.patch` 取回这套,别从零重写。

    private void AnimateSelectedToCenter(int index, int version)
    {
        if (version != _scrollAnimationVersion || LyricList.SelectedIndex != index) return;

        if (LyricList.ContainerFromIndex(index) is not Control container) return;
        if (LyricList.FindDescendantOfType<ScrollViewer>() is not { } scroll) return;
        if (TopLevel.GetTopLevel(this) is not { } topLevel) return;

        var itemHeight = container.Bounds.Height;
        var viewportHeight = scroll.Viewport.Height;
        if (itemHeight <= 0 || viewportHeight <= 0) return; // 布局未就绪

        // 项在内容坐标系中的顶部 = 视口内位置 + 当前偏移
        var pos = container.TranslatePoint(new Point(0, 0), scroll);
        if (pos is null) return;

        var desired = pos.Value.Y + scroll.Offset.Y + itemHeight / 2 - viewportHeight / 2;
        var maxY = Math.Max(0.0, scroll.Extent.Height - viewportHeight);
        var targetY = Math.Clamp(desired, 0, maxY);
        var startY = scroll.Offset.Y;
        if (Math.Abs(targetY - startY) <= 0.5) return;

        TimeSpan? startedAt = null;
        Action<TimeSpan>? tick = null;
        tick = now =>
        {
            if (version != _scrollAnimationVersion || LyricList.SelectedIndex != index) return;
            startedAt ??= now;
            var progress = Math.Clamp((now - startedAt.Value).TotalMilliseconds / 420.0, 0, 1);
            var eased = 1 - Math.Pow(1 - progress, 3); // 平滑缓出，避免逐句跳屏
            scroll.Offset = scroll.Offset.WithY(startY + (targetY - startY) * eased);
            if (progress < 1)
                topLevel.RequestAnimationFrame(tick!);
        };
        topLevel.RequestAnimationFrame(tick);
    }

    private void OnContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container is Control container)
            ApplyProgressClasses(container, e.Index, LyricList.SelectedIndex);
    }

    private void OnLyricListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Pointer.Type != PointerType.Mouse ||
            !e.GetCurrentPoint(LyricList).Properties.IsLeftButtonPressed ||
            e.Source is not Visual source)
            return;

        var container = source as ListBoxItem ?? source.FindAncestorOfType<ListBoxItem>();
        if (container?.DataContext is LyricLine)
            e.Handled = true;
    }

    private void OnLyricListTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Visual source) return;

        var container = source as ListBoxItem ?? source.FindAncestorOfType<ListBoxItem>();
        if (container?.DataContext is not LyricLine line) return;

        var positionMs = DataContext is LyricViewModel vm
            ? vm.GetPlaybackPositionMs(line)
            : Math.Max(0, line.TimeMs);
        SeekRequested?.Invoke(this, new LyricSeekRequestedEventArgs(positionMs));
        e.Handled = true;
    }

    /// <summary>当前句变化后刷新所有已物化容器的方向性进度类。</summary>
    private void UpdateProgressClasses()
    {
        var current = LyricList.SelectedIndex;
        for (var i = 0; i < LyricList.ItemCount; i++)
            if (LyricList.ContainerFromIndex(i) is Control container)
                ApplyProgressClasses(container, i, current);
    }

    /// <summary>映射原版 InteractiveLyricControl2 的 AbovePresent/BelowPresent1/BelowPresent2 状态。
    /// 更远行的弱化交给这三档自己的透明度(0.7 / 0.8 / 0.6 / 0.4),不再另开"远近"类
    /// —— 曾试过按距离摘掉远景那些行的模糊(省约 0.9%),观感有可测的代价而收益只有上界的一半,
    /// 已还原;省模糊改由「性能与体验 → 最佳性能」整档开关承担。</summary>
    private static void ApplyProgressClasses(Control container, int index, int current)
    {
        container.Classes.Set("above", current >= 0 && index < current);
        container.Classes.Set("below1", current >= 0 && index == current + 1);
        container.Classes.Set("below2", current >= 0 && index == current + 2);
    }
}

public sealed class LyricSeekRequestedEventArgs(long positionMs) : EventArgs
{
    public long PositionMs { get; } = positionMs;
}
