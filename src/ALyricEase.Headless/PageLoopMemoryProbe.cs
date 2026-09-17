using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ALyricEase.Controls;
using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.ViewModels;
using ALyricEase.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ALyricEase.Headless;

/// <summary>
/// 个性推荐 ↔ 歌手页 往返循环的内存回归(--memloop 无头 / --memloop-real 真窗口):
/// 每轮"首页 → 歌手页(含封面加载)→ 返回"后强制 GC,检查
/// 1) 上一轮的 ArtistView / RecommendView / 歌曲行 / 专辑卡 是否已被回收(对象泄漏);
/// 2) 托管堆 / 进程私有内存 / 工作集 / 解码图缓存是否逐轮单调上涨(累积泄漏);
/// 3) 视觉树节点数与在屏图片数是否每轮多留一份(控件泄漏);
/// 4) 每轮分配量与推荐页视图实例是否被复用(视图复用是否真的省掉了整页控件重建);
/// 5) 过渡动画未结束就连续往返时,复用缓存是否出现控件双重父级等异常。
/// 封面走探针自带的本地图床(127.0.0.1),不触外网;用过的封面 URL 结束时逐个从
/// 统一媒体缓存删除,不污染真实用户缓存。
/// 真机模式必须跑在 UI 线程并用 Task.Delay 让出线程,否则合成器不渲染(等于没测 GPU 纹理)。
/// </summary>
internal static class PageLoopMemoryProbe
{
    private const int Rounds = 20;
    private const int RealRounds = 15;
    private const int ArtistSongCount = 30;
    private const int ArtistAlbumCount = 50;
    private const int CoverPixels = 320;

    private static readonly List<(string Label, WeakReference Reference)> Pending = new();
    private static readonly List<string> ProbeCoverUrls = new();

    /// <summary>历次捕获到的推荐页视图实例:视图复用生效时应始终只有 1 个活着(其余历史实例都已是垃圾)。</summary>
    private static readonly List<WeakReference> RecommendViewHistory = new();
    private static WeakReference? _previousRecommendView;
    private static bool _recommendViewReused;
    private static long _allocatedBytes;

    /// <summary>分阶段分配量(字节):拆开"进入歌手页"与"返回首页",用于对比首页视图复用的收益。</summary>
    private static readonly List<long> EnterAllocations = new();
    private static readonly List<long> BackAllocations = new();

    /// <summary>上一轮首页首个歌曲行对象的哈希与网格累计重建次数:用于判定复用后首页主体是否仍在重建。</summary>
    private static int _previousRowHash;
    private static int _previousGridRebuilds;
    private static HashSet<int>? _previousRowHashes;
    private static int _previousSectionHash;
    private static HashSet<int>? _previousGridHashes;

    /// <summary>复用视图内部的静态结构节点(上一轮):用于定位"从哪一级开始换新对象"。</summary>
    private static readonly Dictionary<string, object?> StructureNodes = new();

    private static object? LookupStructure(string key) =>
        StructureNodes.TryGetValue(key, out var node) ? node : null;

    private static string NodeState(string label, object? node)
    {
        if (node is null) return $"{label}=?";
        var previous = LookupStructure(label);
        if (previous is null) return $"{label}=初";
        return ReferenceEquals(previous, node) ? $"{label}=同" : $"{label}=换";
    }

    private static void RememberStructure(params (string Label, object? Node)[] nodes)
    {
        foreach (var (label, node) in nodes) StructureNodes[label] = node;
    }

    private static ReusablePageViewTemplate? _reusableTemplate;
    private static bool _recommendViewFromCacheSlot;
    private static int _round;
    private static bool _realMode;

    /// <summary>进入歌手页的方式:false = 直接设 ActivePage(老口径);true = 走用户报的真实点击路径
    /// (点歌曲行「歌手/专辑」按钮 → MenuFlyout → 点「表演者」菜单项)。两种口径其余配置完全相同,
    /// 差值即"这条路径多出来的东西"。</summary>
    private static bool _viaMenu;

    /// <summary>歌手页数据来源:false = 探针注入合成数据(离线口径);true = 让 OpenArtistCommand
    /// 走真实的 LoadAsync,拉真歌手的热门歌曲/专辑与真实封面(ALY_REAL_ARTIST=1)。
    /// 后者才和用户手工操作的数据量一致 —— 合成数据量再接近也只是近似。</summary>
    private static bool _realArtist;

    /// <summary>历次展开过的菜单(弱引用):用于检查菜单对象本身是否被长期持有。</summary>
    private static readonly List<WeakReference> MenuFlyoutHistory = new();
    private static readonly List<int> PopupCounts = new();

    /// <summary>本轮的菜单观察项:菜单交互当下登记,等返回首页、菜单该关掉之后才并入待判定列表 ——
    /// 否则量的是"菜单正开着当然还活着",必然假 FAIL。</summary>
    private static WeakReference? _lastMenuRef;

    /// <summary>窗口夹具是否换成与真实 MainWindow.axaml 一致的 TestMainWindow(ALY_MEMLOOP_REAL_WINDOW=1)。</summary>
    private static bool _realWindowFixture;

    /// <summary>本轮菜单项所在的 MenuFlyoutPresenter(从菜单项逻辑祖先取的强对象观察点)。</summary>
    private static WeakReference? _menuPresenterRef;

    /// <summary>视觉树按类型普查的基线(首次采集时建立,之后每轮报增量)。</summary>
    private static Dictionary<string, int>? _typeBaseline;

    /// <summary>每次"完整阻塞 GC 之后"的水位采样。真实模式下 GC 每 10 轮才来一次,
    /// 中间的 private/工作集波动 ±20MB 会把"每轮只涨一两 MB"的慢泄漏完全盖住 ——
    /// 只有完整回收后的地板值才可比。收尾时把这条序列打出来,趋势一眼可见。</summary>
    private static readonly List<(int Round, double ManagedMb, double PrivateMb, double WorkingSetMb, int Handles, int Threads, int Gdi, int User, int Visuals)> Floors = new();

    /// <summary>无头模式:自驱 UI 队列与渲染时钟。</summary>
    public static int Run()
    {
        try { return RunCoreAsync().GetAwaiter().GetResult(); }
        catch (Exception ex) { Log($"[memloop] 异常: {ex}"); return 1; }
    }

    /// <summary>真机模式:调用方须已运行在 UI 线程,并让出线程驱动真实渲染/合成器。</summary>
    public static async Task<int> RunRealAsync()
    {
        _realMode = true;
        if (Environment.GetEnvironmentVariable("ALY_PAGEVIEW_TRACE") is { Length: > 0 })
            ReusablePageViewTemplate.Trace = message => Log($"[模板] {message}");
        // UI 线程上由 Dispatcher 回调抛出的异常会直接终止进程(没有托管栈可抓),
        // 这里接管并记录:探针要拿到异常内容,而不是让进程静默退出。
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Log($"[memloop] UI 线程未处理异常: {e.Exception}");
            e.Handled = true;
        };
        try { return await RunCoreAsync(); }
        catch (Exception ex) { Log($"[memloop] 异常: {ex}"); return 1; }
    }

    private static async Task<int> RunCoreAsync()
    {
        using var server = new CoverServer(CoverPixels);
        // 进入歌手页的方式由 ALY_MEMLOOP_VIA_MENU 切换:默认沿用"直接设 ActivePage",
        // 设了就走真实点击路径(歌曲行歌手/专辑按钮 → 菜单 → 表演者项)。
        _viaMenu = Environment.GetEnvironmentVariable("ALY_MEMLOOP_VIA_MENU") is { Length: > 0 };
        _realArtist = Environment.GetEnvironmentVariable("ALY_REAL_ARTIST") is { Length: > 0 };
        var main = ServiceLocator.Get<MainViewModel>();
        while (main.CanGoBack) main.GoBackCommand.Execute(null);
        main.ActivePage = "Recommend";

        // 窗口夹具两档:老口径是裸 Window 直接塞 AppShell,真实应用里那层
        // "亚克力边框 + 自绘标题栏 + 常驻 NowPlayingOverlay(+NowPlayingView) + 两个常驻对话框"
        // 全都不存在。ALY_MEMLOOP_REAL_WINDOW=1 改用与真实 MainWindow.axaml 逐字节一致的
        // TestMainWindow,把这层结构补回来 —— 它正是"探针量不到、只有真机才漏"的头号嫌疑。
        _realWindowFixture = Environment.GetEnvironmentVariable("ALY_MEMLOOP_REAL_WINDOW") is { Length: > 0 };
        AppShell shell;
        Window window;
        if (_realWindowFixture)
        {
            window = new TestMainWindow { DataContext = main, ShowActivated = false };
            window.Show();
            await IdleAsync(500);
            shell = window.GetVisualDescendants().OfType<AppShell>().FirstOrDefault()
                    ?? throw new InvalidOperationException("TestMainWindow 里找不到 AppShell");
        }
        else
        {
            shell = new AppShell { DataContext = main };
            window = new Window
            {
                Width = 1200,
                Height = 800,
                ShowActivated = false,
                Content = shell,
            };
            window.Show();
            await IdleAsync(500);
        }

        // 复用模板注册在 AppShell.axaml 上(不在窗口上),两种夹具取法相同
        _reusableTemplate = shell.DataTemplates.OfType<ReusablePageViewTemplate>().FirstOrDefault();

        // 推荐页本身也有内容(每日 30 行 + 3 个卡片区块):返回时这些封面同样会被重建视图重新请求
        FillRecommendContent(server.Port);
        await IdleAsync(200);
        await PumpUntilCoversSettleAsync(server);

        // 预热一轮:JIT/样式/模板/首次布局的一次性开销不计入趋势
        _round = 0;
        await EnterArtistAsync(main, shell, server.Port);
        await BackToRecommendAsync(main, shell);
        Collect();
        Pending.Clear();
        var baselineManaged = GC.GetTotalMemory(true);
        var baselineVisuals = shell.GetVisualDescendants().Count();

        Log($"[memloop] 模式={(_realMode ? "真窗口" : "无头")} " +
            $"窗口夹具={(_realWindowFixture ? "TestMainWindow(真实 MainWindow 结构)" : "裸 Window+AppShell")} " +
            $"进入歌手页={(_viaMenu ? "走歌曲行菜单(真实点击路径)" : "直接设 ActivePage")} " +
            $"歌手页数据={(_realArtist ? "真实网络加载" : "探针注入")} " +
            $"复用模板={(_reusableTemplate is null ? "未注册(XAML 模板表里没有 ReusablePageViewTemplate)" : $"{_reusableTemplate.DataType?.Name}→{_reusableTemplate.ViewType?.Name}")} " +
            $"基线: managed={Mb(baselineManaged):F1}MB private={Mb(PrivateMemory()):F1}MB " +
            $"workingSet={Mb(WorkingSet()):F1}MB visuals={baselineVisuals} coverCache={CoverLabel()}");
        CensusAndShoot(shell, "baseline");

        // 真机模式跑更多轮,且**不再每轮强制 GC** —— 真实使用中没人手动回收,
        // 任务管理器看到的"一直涨"往往就是垃圾堆着没被回收。
        // 轮数可用环境变量 ALY_MEMLOOP_ROUNDS 覆盖:排查阶段跑 1~2 轮就够,正式回归再用默认值。
        var roundsOverride = Environment.GetEnvironmentVariable("ALY_MEMLOOP_ROUNDS");
        var rounds = int.TryParse(roundsOverride, out var configured) && configured > 0
            ? configured
            : _realMode ? RealRounds : Rounds;
        var failures = 0;
        var previousManaged = baselineManaged;
        var previousPrivate = PrivateMemory();
        _allocatedBytes = GC.GetTotalAllocatedBytes(false);
        for (var round = 1; round <= rounds; round++)
        {
            _round = round;
            // 分阶段计量:整轮分配量会被"歌手页重建"淹没,看不出首页复用的收益。
            // 这里把"进入歌手页"和"返回首页"两次导航的分配量拆开,才能直接对比复用与不复用
            // 在"返回"这一步上的代价(返回首页只该重建首页那棵树,不复用时每轮都要重建)。
            var enterStart = GC.GetTotalAllocatedBytes(false);
            await EnterArtistAsync(main, shell, server.Port);
            await PumpUntilCoversSettleAsync(server);
            var enterAllocated = GC.GetTotalAllocatedBytes(false) - enterStart;
            var peakCache = CoverImagePipeline.MemoryCacheStats;
            var peakPrivate = PrivateMemory();
            var peakWorkingSet = WorkingSet();
            var onScreenImages = shell.GetVisualDescendants().OfType<Image>().Count();
            var artistVisuals = shell.GetVisualDescendants().Count();

            var backStart = GC.GetTotalAllocatedBytes(false);
            await BackToRecommendAsync(main, shell);
            var backAllocated = GC.GetTotalAllocatedBytes(false) - backStart;
            // 返回必须真的发生:GoBack 在 _navigationHistory 为空时会直接 return(静默空操作)。
            // 万一历史没被压栈,这一整轮就退化成"反复进歌手页、从未返回",结论全废 —— 所以每轮写实。
            var pageAfterBack = main.ActivePage;
            var canGoBackAfterBack = main.CanGoBack;


            var beforeCollect = GC.GetTotalMemory(false);
            // 真机模式每 10 轮才回收一次:看 GC 不来时工作集是否阶梯式上涨
            var collectNow = !_realMode || round % 10 == 0;
            if (collectNow) Collect();

            var managed = GC.GetTotalMemory(false);
            var priv = PrivateMemory();
            var cache = CoverImagePipeline.MemoryCacheStats;
            var visuals = shell.GetVisualDescendants().Count();
            var leaked = Leaked();

            // 返回首页后真正画上封面的 Image 数:视图被复用时,离树时释放过位图租约的图片
            // 必须能重新加载,否则首页会退回"一片占位底色"(视图复用最危险的回归)。
            var paintedCovers = shell.GetVisualDescendants().OfType<Image>()
                .Count(image => image.Source is not null);
            // 首页内容完整性:视图复用会跳过整页重建,必须每轮确认首页的歌曲行/卡片没少
            var homeNodes = shell.GetVisualDescendants();
            var homeRows = homeNodes.OfType<TrackRow>().Count();
            var homeCards = homeNodes.OfType<Button>().Count(button => button.Classes.Contains("card"));

            // 关键判据:视图复用只保住"页面骨架",首页主体(90 个歌曲行)是数据驱动的 ——
            // 只要页 VM 每次返回都新建行 VM,SongGridView 就会整列重建,复用等于没省。
            // 这里同时看"首行控件是否还是同一个对象"和"网格累计重建次数",把这件事量化。
            var firstRow = homeNodes.OfType<TrackRow>().FirstOrDefault();
            var rowHash = firstRow is null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(firstRow);
            var gridRebuilds = homeNodes.OfType<SongGridView>().Sum(grid => grid.RebuildCount);
            // 比"首行是否同一个对象"更硬:整页行控件的哈希集合,和上一轮求差,得到"本轮新造了多少个行控件"。
            // 视图复用若真省下了首页主体,这个数应当在稳态降到 0。
            var rowHashes = homeNodes.OfType<TrackRow>()
                .Select(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode)
                .ToHashSet();
            var newRows = _previousRowHashes is null
                ? rowHashes.Count
                : rowHashes.Count(hash => !_previousRowHashes.Contains(hash));
            _previousRowHashes = rowHashes;

            // 内容子树为什么重建:要么页 VM 把区块集合换了一批(数据重载),要么只是控件容器被重新实化。
            // 两者修法完全不同,所以要分开量:区块首项 VM 是否还是同一个对象 + 网格控件是否换新。
            var recommendVm = ServiceLocator.Get<RecommendViewModel>();
            var sectionHash = recommendVm.Sections.Count > 0
                ? RuntimeHelpers.GetHashCode(recommendVm.Sections[0]) : 0;
            var sectionState = _previousSectionHash == 0
                ? "-" : sectionHash == _previousSectionHash ? "未重建" : "已重建";
            _previousSectionHash = sectionHash;
            var gridHashes = homeNodes.OfType<SongGridView>()
                .Select(RuntimeHelpers.GetHashCode).ToHashSet();
            var gridsReused = _previousGridHashes is not null
                && gridHashes.SetEquals(_previousGridHashes);
            _previousGridHashes = gridHashes;

            // 逐级定位:"从哪一层开始换新对象"。视图是复用的,若连 PageContent 这种 AXAML 静态节点
            // 都换了,说明是整棵模板重挂;若只有生成出来的容器换了,说明是 ItemsControl 的容器生成器被重置。
            var pageContent = homeNodes.OfType<StackPanel>().FirstOrDefault(panel => panel.Name == "PageContent");
            var sectionsHost = homeNodes.OfType<ItemsControl>()
                .FirstOrDefault(control => ReferenceEquals(control.ItemsSource, recommendVm.Sections));
            var presenter = sectionsHost?.GetVisualDescendants().OfType<ItemsPresenter>().FirstOrDefault();
            var hostPanel = presenter?.Panel;
            var firstContainer = hostPanel?.Children.FirstOrDefault();
            var structure = string.Join(" ", new[]
            {
                NodeState("PageContent", pageContent),
                NodeState("区块列表", sectionsHost),
                NodeState("呈现器", presenter),
                NodeState("面板", hostPanel),
                NodeState("首区块容器", firstContainer),
            });
            RememberStructure(("PageContent", pageContent), ("区块列表", sectionsHost),
                ("呈现器", presenter), ("面板", hostPanel), ("首区块容器", firstContainer));
            var rowState = _previousRowHash == 0
                ? "-"
                : rowHash == _previousRowHash ? "未重建" : "已重建";
            var rebuildDelta = gridRebuilds - _previousGridRebuilds;

            // 每轮分配量:视图重建的总代价在于"每次都新造一棵树"。
            // 复用视图后这个数字应显著下降(不再重新构造整页控件),它比内存水位更早暴露问题。
            var allocated = GC.GetTotalAllocatedBytes(false);
            var allocatedDelta = allocated - _allocatedBytes;
            _allocatedBytes = allocated;
            // 复用生效时,历次捕获应全部指向同一个实例;这里统计"不同实例数"而不是弱引用条数
            // (弱引用条数只反映垃圾有没有被回收,复用场景下所有条目本就是同一个对象)。
            var recommendInstances = RecommendViewHistory
                .Select(reference => reference.Target)
                .Where(target => target is not null)
                .Distinct()
                .Count();

            Log(
                $"[memloop] 第 {round} 轮: 本轮分配={Mb(allocatedDelta):F1}MB " +
                $"(进入歌手页={Mb(enterAllocated):F1}MB 返回首页={Mb(backAllocated):F1}MB) " +
                $"managed={Mb(managed):F1}MB (Δ{Mb(managed - previousManaged):+0.0;-0.0}) " +
                $"未GC={Mb(beforeCollect):F1}MB " +
                $"private={Mb(priv):F1}MB (Δ{Mb(priv - previousPrivate):+0.0;-0.0}) " +
                $"workingSet={Mb(WorkingSet()):F1}MB{(collectNow ? " [已GC]" : "")} " +
                $"返回后页面={pageAfterBack}/可回退={canGoBackAfterBack} {WindowCensus()} " +
                $"菜单呈现器={MenuPresenterCount(shell)}" +
                (MenuFlyoutHistory.Count > 0
                    ? $"(历次菜单存活参考={MenuFlyoutHistory.Count(reference => reference.IsAlive)}/{MenuFlyoutHistory.Count}" +
                      $" 本轮菜单在返回后={LastMenuState()} 菜单呈现器在返回后={MenuPresenterState()})"
                    : "") + " " +
                $"歌手页[visuals={artistVisuals} images={onScreenImages} 缓存={peakCache.Items}项/{Mb(peakCache.Bytes):F0}MB " +
                $"private峰值={Mb(peakPrivate):F0}MB 工作集峰值={Mb(peakWorkingSet):F0}MB 图床累计={server.RequestCount}] " +
                $"返回后[visuals={visuals} 首页行={homeRows} 卡={homeCards} 已上屏封面={paintedCovers} 缓存={cache.Items}项/{Mb(cache.Bytes):F0}MB " +
                $"首页首行={rowState} 网格重建={gridRebuilds}(本轮+{rebuildDelta}) 新增行控件={newRows}/{rowHashes.Count} " +
                $"区块首项={sectionState} 网格控件={(gridsReused ? "同上轮" : "换新")} 结构[{structure}] " +
                $"推荐页视图实例={recommendInstances}/{RecommendViewHistory.Count}次捕获 " +
                $"复用={(_recommendViewReused ? "是" : "否")} 缓存槽命中={(_recommendViewFromCacheSlot ? "是" : "否")}] " +
                $"累计遗留={leaked.Count}/{Pending.Count}" +
                (leaked.Count > 0 ? " [" + string.Join(",", leaked) + "]" : ""));

            // 节点总数持平也可能是"少的和多的互相抵消",这行把差异按类型摊开
            Log($"[memloop] R{round} 类型普查: {TypeCensusDelta(shell)}");

            // 完整回收后的地板值才是可比量:中间轮次的 ±20MB 波动全是"垃圾还没收"。
            if (collectNow)
            {
                var floorProcess = Process.GetCurrentProcess();
                Floors.Add((round, Mb(managed), Mb(priv), Mb(WorkingSet()), floorProcess.HandleCount,
                    floorProcess.Threads.Count, (int)GetGuiResources(floorProcess.Handle, 0),
                    (int)GetGuiResources(floorProcess.Handle, 1), visuals));
            }

            // 只有"刚做过完整回收"的轮次才拿遗留数判泄漏:
            // 未回收轮里弱引用还活着只是垃圾堆着,不代表有人长期持有。
            if (collectNow && leaked.Count > 0) failures++;
            // 走菜单那条路径时,返回首页后菜单呈现器不该还挂在视觉树上:
            // 挂着就是"弹出层没被摘掉",这是真实点击路径独有的失败模式(直接设 ActivePage 不会发生)。
            // ⚠ 别用叠加层里数 MenuFlyoutPresenter 来判:Avalonia 12 的菜单不走 PopupRoot,
            // 那个计数恒为 0,判据会变成永远不触发的死条件(实测)。改为直接看从菜单项取到的呈现器。
            if (collectNow && MenuPresenterState() == "仍在树") failures++;
            // 复用生效时,除"当前这一个"(最多再加一个过渡中的旧实例)之外不应有别的推荐页视图存在
            if (collectNow && recommendInstances > 2) failures++;
            previousManaged = managed;
            previousPrivate = priv;
            EnterAllocations.Add(enterAllocated);
            BackAllocations.Add(backAllocated);
            _previousRowHash = rowHash;
            _previousGridRebuilds = gridRebuilds;
        }

        // 稳态对比用:取后半程中位数,避开前几轮"封面首次下载/解码"的一次性开销。
        Log($"[memloop] 分阶段分配中位数(全轮): 进入歌手页={Mb(Median(EnterAllocations)):F1}MB 返回首页={Mb(Median(BackAllocations)):F1}MB");
        var steadyEnter = Median(EnterAllocations.Skip(EnterAllocations.Count / 2).ToList());
        var steadyBack = Median(BackAllocations.Skip(BackAllocations.Count / 2).ToList());
        Log($"[memloop] 分阶段分配中位数(后半程): 进入歌手页={Mb(steadyEnter):F1}MB 返回首页={Mb(steadyBack):F1}MB");

        // 逐轮波动看趋势没意义(±20MB 的垃圾堆),这里只列"完整阻塞 GC 之后"的地板值。
        // 真泄漏:地板单调上行。只是分配抖动:地板在一条水平线附近来回。
        if (Floors.Count > 0)
        {
            Log("[memloop] 完整GC后地板值序列(轮的 managed/private/工作集/句柄/线程/GDI/USER/视觉节点):");
            foreach (var floor in Floors)
            {
                Log($"[memloop]   地板 R{floor.Round,-3} managed={floor.ManagedMb,6:F1}MB " +
                    $"private={floor.PrivateMb,7:F1}MB 工作集={floor.WorkingSetMb,7:F1}MB " +
                    $"句柄={floor.Handles} 线程={floor.Threads} GDI={floor.Gdi} USER={floor.User} 视觉节点={floor.Visuals}");
            }
            if (Floors.Count >= 2)
            {
                var first = Floors[0];
                var last = Floors[^1];
                var span = last.Round - first.Round;
                Log($"[memloop] 地板趋势 R{first.Round}→R{last.Round}(跨 {span} 轮): " +
                    $"private {first.PrivateMb:F1}→{last.PrivateMb:F1}MB ({(span > 0 ? (last.PrivateMb - first.PrivateMb) / span : 0):+0.00;-0.00}MB/轮) " +
                    $"工作集 {first.WorkingSetMb:F1}→{last.WorkingSetMb:F1}MB ({(span > 0 ? (last.WorkingSetMb - first.WorkingSetMb) / span : 0):+0.00;-0.00}MB/轮) " +
                    $"句柄 {first.Handles}→{last.Handles} 线程 {first.Threads}→{last.Threads} " +
                    $"GDI {first.Gdi}→{last.Gdi} USER {first.User}→{last.User} 视觉节点 {first.Visuals}→{last.Visuals}");
            }
        }

        // 循环结束后的首页:复用生效时应与基线完全一致(节点数/歌曲行数/卡片数/已上屏图)
        CensusAndShoot(shell, $"after-{rounds}rounds");

        // 过渡动画没结束就连续往返:复用视图必须经得起"同一实例被两个父容器争用"的时序
        Log("[memloop] 进入快速往返阶段");
        var rapidFailures = await RapidRoundTripAsync(main, shell);
        Log("[memloop] 快速往返阶段结束");

        // 给在途异步(红心查询、封面下载超时等)足够窗口结束,再判定是否为真泄漏
        await IdleAsync(8000);
        Collect();
        var lingering = Leaked();
        var recommendInstancesFinal = RecommendViewHistory
            .Select(reference => reference.Target).Where(target => target is not null).Distinct().Count();
        Log($"[memloop] 收尾: 遗留对象={lingering.Count}/{Pending.Count} " +
            $"{(lingering.Count > 0 ? "[" + string.Join(",", lingering) + "] " : "")}" +
            $"菜单存活={MenuFlyoutHistory.Count(reference => reference.IsAlive)}/{MenuFlyoutHistory.Count} " +
            $"弹出层呈现器={MenuPresenterCount(shell)} " +
            $"推荐页视图实例={recommendInstancesFinal}/{RecommendViewHistory.Count}次捕获 " +
            $"缓存={CoverLabel()} private={Mb(PrivateMemory()):F1}MB " +
            $"workingSet={Mb(WorkingSet()):F1}MB");
        Log($"[memloop] 收尾类型普查: {TypeCensusDelta(shell)}");

        await IdleAsync(300);
        window.Close();
        await IdleAsync(300);
        ReleaseProbeContent();
        CleanupCoverCache();

        Log(failures == 0 && lingering.Count == 0 && rapidFailures == 0
            ? "[memloop] PASS: 无对象被长期持有"
            : $"[memloop] FAIL: 已回收轮仍遗留 {failures} 轮, 收尾残留={lingering.Count}, 快速往返异常={rapidFailures}");
        return failures == 0 && lingering.Count == 0 && rapidFailures == 0 ? 0 : 1;
    }

    /// <summary>过渡动画(300ms)进行到 1/5 就返回,连续 10 次:
    /// 此时上一棵推荐页视图仍挂在正在退场的 presenter 上,若复用缓存把它直接交给新 presenter,
    /// 就会出现同一控件两个父级(异常/视觉树错乱)。这一段专门制造并观察该时序。
    /// 本阶段不填充歌手页数据:只关心容器与视图的挂载时序,不关心数据量。</summary>
    private static async Task<int> RapidRoundTripAsync(MainViewModel main, AppShell shell)
    {
        var exceptions = new List<string>();
        for (var index = 0; index < 10; index++)
        {
            try
            {
                main.ActivePage = "Artist";
                await IdleAsync(60);
                main.GoBackCommand.Execute(null);
                await IdleAsync(60);
            }
            catch (Exception ex)
            {
                exceptions.Add($"{ex.GetType().Name}: {ex.Message}");
                // 崩在这里时日志要留下"第几步",否则只剩一个退出码
                Log($"[memloop] 快速往返第 {index + 1} 次异常: {ex.GetType().Name}: {ex.Message}");
            }
        }

        await IdleAsync(600);
        if (!string.Equals(main.ActivePage, "Recommend", StringComparison.Ordinal))
            main.ActivePage = "Recommend";
        await IdleAsync(600);

        var instances = RecommendViewHistory
            .Select(reference => reference.Target).Where(target => target is not null).Distinct().Count();
        Log($"[memloop] 快速往返(过渡未结束即返回) x10: 异常={exceptions.Count}" +
            (exceptions.Count > 0
                ? " [" + string.Join(" | ", exceptions.Distinct().Take(3)) + "]"
                : "") +
            $" 推荐页视图实例={instances}/{RecommendViewHistory.Count}次捕获 " +
            $"visuals={shell.GetVisualDescendants().Count()}");
        return exceptions.Count;
    }

    private static string CoverLabel()
    {
        var stats = CoverImagePipeline.MemoryCacheStats;
        return $"{stats.Items}项/{Mb(stats.Bytes):F1}MB";
    }

    /// <summary>弹出层普查:菜单展开时会把 MenuFlyoutPresenter 挂到 OverlayLayer 上。
    /// 关不掉 / 回收不掉的菜单会在这里逐轮累加,是"一直重复一直加"最直接的指纹。</summary>
    private static int MenuPresenterCount(AppShell shell) => OverlayCensus(shell).Presenters;

    /// <summary>⚠ 菜单开着时 MenuFlyoutPresenter 挂在 OverlayLayer 上,**不是** TopLevel 的直接后代 ——
    /// 早先按 `TopLevel.GetVisualDescendants().OfType&lt;MenuFlyoutPresenter&gt;()` 数,
    /// 结果是"菜单明明已展开、计数恒为 0",那条失败判据等于死条件。
    /// 必须从 `OverlayLayer.GetOverlayLayer(topLevel)` 进去数,并把 PopupRoot 一起报出来定位。</summary>

    private static (int Popups, int Presenters) OverlayCensus(AppShell shell)
    {
        var topLevel = TopLevel.GetTopLevel(shell);
        if (topLevel is null) return (-1, -1);
        var overlay = OverlayLayer.GetOverlayLayer(topLevel);
        if (overlay is null) return (0, 0);
        var descendants = overlay.GetVisualDescendants().ToList();
        return (descendants.OfType<PopupRoot>().Count(),
                descendants.OfType<MenuFlyoutPresenter>().Count());
    }

    /// <summary>本轮菜单在"返回首页之后"的状态:已关闭=正常;仍开着=弹出层没被关掉;
    /// 已回收=对象也没了。三者对应完全不同的修法,所以要分开报。</summary>
    private static string LastMenuState() => _lastMenuRef?.Target is MenuFlyout flyout
        ? flyout.IsOpen ? "仍开着" : "已关闭"
        : "已回收";

    /// <summary>本轮菜单呈现器在"返回首页之后"的下落:仍挂在视觉树上=弹出层没被摘掉(真嫌疑);
    /// 已脱树=没挂树但对象还在;已回收=对象都没了(最干净)。</summary>
    /// 判"在不在视觉树上"用 `TopLevel.GetTopLevel(...)` 是不是 null —— 与
    /// `FlyoutOpenAnimation` 里"脱树"的判法逐字一致,别写第二套口径
    /// (`Visual.GetVisualRoot()` 在本项目引用的 Avalonia 版本里不是可用的扩展方法)。
    private static string MenuPresenterState() => _menuPresenterRef?.Target is Visual presenter
        ? TopLevel.GetTopLevel(presenter) is not null ? "仍在树" : "已脱树"
        : "已回收";

    /// <summary>视觉树按控件类型普查:节点总数一样不代表没泄漏 ——
    /// 可能"少了一个行控件、多了三个别的东西"互相抵消。
    /// 逐轮打印相对首次采集的增量,能把"到底是哪一类控件在累加"直接指出来,
    /// 不用再靠逐个弱引用猜。首次调用建立基线。</summary>
    private static string TypeCensusDelta(Visual root)
    {
        var current = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var node in root.GetVisualDescendants())
        {
            var name = node.GetType().Name;
            current[name] = current.TryGetValue(name, out var value) ? value + 1 : 1;
        }

        if (_typeBaseline is null)
        {
            _typeBaseline = current;
            return $"类型基线已建({current.Count}类/{current.Values.Sum()}个)";
        }

        var changes = current
            .Select(pair => (Name: pair.Key, Delta: pair.Value - _typeBaseline.GetValueOrDefault(pair.Key)))
            .Where(item => item.Delta != 0)
            .OrderByDescending(item => Math.Abs(item.Delta))
            .Take(8)
            .Select(item => $"{item.Name}{item.Delta:+#;-#}")
            .ToList();

        var gone = _typeBaseline.Keys.Where(key => !current.ContainsKey(key)).Take(4).Select(key => $"{key}-全消失");
        var merged = changes.Concat(gone).ToList();
        return merged.Count == 0
            ? $"类型分布不变({current.Count}类/{current.Values.Sum()}个)"
            : $"类型变化[{string.Join(",", merged)}] 总数={current.Values.Sum()}(基线{_typeBaseline.Values.Sum()})";
    }

    /// <summary>进程句柄数与顶层窗口数:弹出层若在 Windows 上落了真实窗口/HWND,
    /// 关不掉就会在这里逐轮累加 —— 无头模式没有这条通道,所以只有在真窗口模式下才量得到。
    /// 同时读 GDI/USER 对象数:句柄不落、但 GDI/USER 一直涨,说明泄漏在图形资源那一侧。</summary>
    private static string WindowCensus()
    {
        var windows = Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.Windows.Count
            : -1;
        var process = Process.GetCurrentProcess();
        // 线程数一并量:每次导航悄悄起一个后台循环/线程池常驻线程,句柄和 GDI 都不会动,
        // 只有线程数会阶梯式上涨 —— 这是"一直重复一直加"的另一类指纹。
        return $"句柄={process.HandleCount} 线程={process.Threads.Count} " +
               $"GDI={GetGuiResources(process.Handle, 0)} " +
               $"USER={GetGuiResources(process.Handle, 1)} 顶层窗口={windows}";
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr process, uint flags);

    private static long PrivateMemory() => Process.GetCurrentProcess().PrivateMemorySize64;    private static long WorkingSet() => Process.GetCurrentProcess().WorkingSet64;

    /// <summary>按真实导航路径进入歌手页并填满内容。
    /// 两种入口:直接设 ActivePage(--memloop-real 老口径),或走歌曲行菜单的真实点击路径
    /// (ALY_MEMLOOP_VIA_MENU=1),两者除"点不点那个按钮"外完全一致。</summary>
    private static async Task EnterArtistAsync(MainViewModel main, AppShell shell, int port)
    {
        var artist = ServiceLocator.Get<ArtistViewModel>();
        if (!_realArtist) FillArtistContent(artist, port);
        if (_viaMenu)
        {
            await EnterArtistViaRowMenuAsync(main, shell);
            // 真实命令路径会触发一次加载(探针无网络,失败可能清空集合):
            // 补回内容,保证两模式在"歌手页有多少数据"上一致,否则内存差里混了内容量差。
            if (!_realArtist) FillArtistContent(artist, port);
        }
        else
        {
            main.ActivePage = "Artist";
        }
        await IdleAsync(120);
        CaptureView<ArtistView>(shell, "歌手页视图");
        if (_realArtist)
            Log($"[memloop] R{_round} 歌手页真实数据: 歌曲={artist.Songs.Count} 专辑={artist.Albums.Count} " +
                $"单曲={artist.Singles.Count} 名字=「{artist.Name}」头像={(artist.AvatarUrl.Length > 0 ? "有" : "无")}");
        // VM 层对象:集合被 Clear 后应可回收,否则单例 VM 会逐轮累积整页数据
        if (artist.Songs.Count == 0)
        {
            // 真实口径下网络/登录偶发失败是常态(实测第 7 轮就撞到过一次):这种轮次数据量不同源,
            // 不能和别的轮比,也不该把整条寿命打断 —— 只跳过本轮观测,继续跑满轮数。
            if (_realArtist)
            {
                Log($"[memloop] R{_round} 真实歌手页本次没加载到数据(网络/登录瞬时失败),本轮不参与观测");
                return;
            }
            throw new InvalidOperationException("歌手页歌曲集合为空,后续弱引用观察会越界");
        }
        Observe($"R{_round}/歌曲行", new WeakReference(artist.Songs[0]));
        if (artist.Albums.Count > 0) Observe($"R{_round}/专辑卡", new WeakReference(artist.Albums[0]));
    }

    /// <summary>用户报的那条路径:点每日推荐歌曲行里的「歌手/专辑」按钮 → MenuFlyout 展开 →
    /// 点「表演者: xxx」菜单项 → OpenArtistCommand → 歌手页。
    /// 老口径(--memloop-real)直接设 ActivePage,从不打开任何弹出层 —— 这正是两条路径的唯一差别,
    /// 也是"一直重复一直加"只在手工操作里出现的原因。</summary>
    private static async Task EnterArtistViaRowMenuAsync(MainViewModel main, AppShell shell)
    {
        var row = shell.GetVisualDescendants().OfType<TrackRow>()
            .FirstOrDefault(candidate => candidate.DataContext is SongItemViewModel { HasArtist: true });
        Assert(row is not null, "推荐页里找不到「有歌手可跳转」的歌曲行");

        var button = row.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(candidate => candidate.Name == "ArtistAlbumButton");
        Assert(button is not null, "歌曲行里找不到 ArtistAlbumButton(当前主题没有此部件)");
        if (button.Resources["TrackMenuFlyout"] is not MenuFlyout flyout)
            throw new InvalidOperationException("按钮资源里找不到 TrackMenuFlyout");

        // 真实点击:走 TrackRow.OnArtistAlbumClick → flyout.ShowAt(button)
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await IdleAsync(200);

        var artistItem = flyout.Items.OfType<MenuItem>().FirstOrDefault();
        Assert(artistItem is not null, "展开的菜单里没有菜单项");
        var topLevel = TopLevel.GetTopLevel(shell);
        var openCensus = OverlayCensus(shell);
        // 更硬、不依赖弹层实现方式的判据:从菜单项往上拿它所在的呈现器本身。
        // Avalonia 12 的菜单不走 PopupRoot(实测叠加层里 PopupRoot/呈现器都数不到),
        // 只能抓对象:返回首页后这个呈现器还挂在视觉树上,就是"弹出层没被回收"。
        var presenter = artistItem.GetLogicalAncestors().OfType<MenuFlyoutPresenter>().FirstOrDefault();
        _menuPresenterRef = presenter is null ? null : new WeakReference(presenter);
        Log($"[memloop] R{_round} 菜单: 已展开={flyout.IsOpen} 项数={flyout.Items.Count} " +
            $"首项=「{artistItem.Header}」可执行={artistItem.Command?.CanExecute(artistItem.CommandParameter)} " +
            $"叠加层: PopupRoot={openCensus.Popups} 呈现器={openCensus.Presenters} " +
            $"菜单呈现器={(presenter is null ? "取不到" : "已取到")} " +
            $"展开时在树上={presenter is not null && TopLevel.GetTopLevel(presenter) is not null}");

        MenuFlyoutHistory.Add(new WeakReference(flyout));
        _lastMenuRef = new WeakReference(flyout);
        // 注意:菜单/菜单项**不能**当泄漏判据 —— 它们就在这个歌曲行的 Button.Resources 里,
        // 行控件还挂在树上时它们当然活着(行每轮重建,旧的自然回收)。真正要判的是
        // "返回之后弹出层有没有留在 OverlayLayer 上"(见 MenuPresenterCount)与"菜单关没关"。

        var before = main.ActivePage;
        // MenuItem 的真实点击由指针抬起事件驱动,入口是受保护方法,公开 API 没有等价物。
        // 先打印一次候选签名(口径依赖 Avalonia 版本),再按签名构造参数调用。
        if (_round <= 1)
            Log("[memloop] MenuItem 点击候选方法: " + string.Join(" | ", typeof(MenuItem)
                .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .Where(method => method.Name.Contains("Click", StringComparison.Ordinal)
                              || method.Name.Contains("Invoke", StringComparison.Ordinal))
                .Select(method => $"{method.Name}({string.Join(",", method.GetParameters().Select(p => p.ParameterType.Name))})")));
        var invoked = InvokeMenuItemClick(artistItem);
        await IdleAsync(240);
        if (main.ActivePage == before
            && artistItem.Command is { } command
            && command.CanExecute(artistItem.CommandParameter))
        {
            Log($"[memloop] R{_round} 受保护点击入口已调用={invoked} 但未触发导航,退回直接执行菜单项命令");
            command.Execute(artistItem.CommandParameter);
            await IdleAsync(200);
        }

        // 导航发生在"菜单正开着"的时序里:这里记录菜单有没有被关掉 ——
        // 没关掉 + 还挂在弹出层上,就是逐轮累积的直接嫌疑。
        var stillOpen = flyout.IsOpen;
        var afterCensus = OverlayCensus(shell);
        Log($"[memloop] R{_round} 点菜单项后: ActivePage={main.ActivePage} " +
            $"菜单仍开着={stillOpen} 叠加层: PopupRoot={afterCensus.Popups} 呈现器={afterCensus.Presenters}");
        Assert(main.ActivePage == "Artist", "点「表演者」菜单项后没有进入歌手页");
    }

    /// <summary>点菜单项。Avalonia 的 MenuItem 由指针抬起事件驱动那个受保护的点击处理器,
    /// 公开 API 没有等价入口;这里按签名构造参数调用同一个处理器(只差指针事件本身),
    /// 签名不匹配时返回 false,由调用方退回"直接执行菜单项命令"。</summary>
    private static bool InvokeMenuItemClick(MenuItem item)
    {
        foreach (var method in typeof(MenuItem)
                     .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
                     .Where(candidate => candidate.Name == "OnClick"))
        {
            var parameters = method.GetParameters();
            if (parameters.Length != 1) continue;
            if (parameters[0].ParameterType != typeof(RoutedEventArgs)) continue;
            method.Invoke(item, [new RoutedEventArgs(MenuItem.ClickEvent)]);
            return true;
        }
        return false;
    }

    /// <summary>返回个性推荐(真实返回命令:弹出历史 + 新建或复用 RecommendView)。</summary>
    private static async Task BackToRecommendAsync(MainViewModel main, AppShell shell)
    {
        main.GoBackCommand.Execute(null);
        await IdleAsync(480); // 页面过渡 300ms:过渡结束后旧视图才从视觉树摘除

        // 过渡期间新旧两棵可能同时在树里(退场的那棵已隐藏):必须取"当前页"那一棵,
        // 否则实例比对会拿隐藏的旧树去比,复用判定失真。
        var views = shell.GetVisualDescendants().OfType<RecommendView>().ToList();
        var view = views.Where(candidate => candidate.IsVisible)
                        .OrderByDescending(candidate => candidate.GetVisualDescendants().Count())
                        .FirstOrDefault()
                    ?? views.FirstOrDefault();
        Assert(view is not null, "导航后视觉树里找不到推荐页视图");
        Log($"[memloop] 树中推荐页视图 {views.Count} 个: " + string.Join(",", views.Select(
            candidate => $"#{RuntimeHelpers.GetHashCode(candidate)}(可见={candidate.IsVisible},节点={candidate.GetVisualDescendants().Count()})")));

        _recommendViewReused = ReferenceEquals(_previousRecommendView?.Target, view);
        _recommendViewFromCacheSlot = ReferenceEquals(_reusableTemplate?.CachedView, view);
        _previousRecommendView = new WeakReference(view);
        RecommendViewHistory.Add(new WeakReference(view));
        Observe($"R{_round}/推荐页视图", new WeakReference(view));
    }

    private static void Observe(string label, WeakReference reference) =>
        Pending.Add((label, reference));

    /// <summary>同时写控制台与环境变量 ALY_PROBE_LOG 指定的文件:
    /// 真机模式跑在 GUI 进程里,若中途崩在渲染线程上,管道里的尾部输出会丢,落盘才能事后复盘。</summary>
    private static void Log(string message)
    {
        Console.WriteLine(message);
        var path = Environment.GetEnvironmentVariable("ALY_PROBE_LOG");
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            File.AppendAllText(path, message + Environment.NewLine);
        }
        catch
        {
            // 日志失败不影响探针判定
        }
    }

    /// <summary>首页节点普查 + 可选截屏:视图复用的最大风险不是内存,而是"页面回来了但内容少了"。
    /// 复用会跳过整页控件的重建,所以必须用实际节点数与真实渲染结果确认内容完整。
    /// 截屏目录由 ALY_PROBE_SHOTS 指定,不设则只打印数字。</summary>
    private static void CensusAndShoot(AppShell shell, string tag)
    {
        var nodes = shell.GetVisualDescendants().ToList();
        var rows = nodes.OfType<TrackRow>().Count();
        var cards = nodes.OfType<Button>().Count(button => button.Classes.Contains("card"));
        var painted = nodes.OfType<Image>().Count(image => image.Source is not null);
        Log($"[memloop] 首页普查({tag}): 节点={nodes.Count} 歌曲行={rows} 卡片={cards} 已上屏图={painted}");

        var directory = Environment.GetEnvironmentVariable("ALY_PROBE_SHOTS");
        if (string.IsNullOrEmpty(directory)) return;
        try
        {
            var size = new PixelSize(
                Math.Max(1, (int)Math.Ceiling(shell.Bounds.Width)),
                Math.Max(1, (int)Math.Ceiling(shell.Bounds.Height)));
            using var bitmap = new RenderTargetBitmap(size);
            bitmap.Render(shell);
            var path = Path.Combine(directory, $"memloop-{tag}.png");
            bitmap.Save(path);
            Log($"[memloop] 首页截图: {path}");
        }
        catch (Exception ex)
        {
            Log($"[memloop] 截图失败: {ex.Message}");
        }
    }

    /// <summary>仍存活且已离开视觉树的受观对象(在树上的当前页视图不算泄漏)。</summary>
    private static List<string> Leaked()
    {
        var leaked = new List<string>();
        foreach (var (label, reference) in Pending)
        {
            if (!reference.IsAlive) continue;
            if (reference.Target is Avalonia.Visual visual && visual.IsAttachedToVisualTree()) continue;
            leaked.Add(label);
        }
        return leaked;
    }

    /// <summary>推进 UI 队列与渲染:无头模式手动推帧;真机模式让出线程给真实合成器。</summary>
    private static async Task IdleAsync(int milliseconds)
    {
        if (_realMode)
        {
            await Task.Delay(milliseconds);
            return;
        }

        var deadline = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(10);
        }
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>等封面真正解码完成:(缓存条目, 图床请求数)连续 2 秒不变且已解出图,或最长 20 秒。</summary>
    private static async Task PumpUntilCoversSettleAsync(CoverServer server)
    {
        var deadline = Environment.TickCount64 + 20_000;
        var lastSignature = "";
        var lastChange = Environment.TickCount64;
        while (Environment.TickCount64 < deadline)
        {
            await IdleAsync(80);

            var stats = CoverImagePipeline.MemoryCacheStats;
            var signature = $"{stats.Items}/{server.RequestCount}";
            if (signature != lastSignature)
            {
                lastSignature = signature;
                lastChange = Environment.TickCount64;
            }
            else if (Environment.TickCount64 - lastChange > 2000 && stats.Items > 0)
            {
                break;
            }
        }
        await IdleAsync(80);
    }

    private static void FillArtistContent(ArtistViewModel artist, int port)
    {
        if (artist.Songs.Count == 0)
        {
            var player = ServiceLocator.Get<PlayerViewModel>();
            var queue = Enumerable.Range(1, ArtistSongCount).Select(index => new Song
            {
                Id = 800_000 + index,
                Source = MusicSource.NetEase,
                Name = $"探针歌曲 {index:D2}",
                Artist = "探针歌手",
                Album = "探针专辑",
                DurationMs = 180_000,
                ArtistIds = [6452],
                ArtistNames = ["探针歌手"],
                AlbumId = 70_000,
                CoverUrl = CoverUrl(port, $"song-{index}"),
            }).ToList();
            foreach (var song in queue)
                artist.Songs.Add(new SongItemViewModel(
                    song, player.PlayFromList, queue: queue, source: "探针歌手"));
        }

        if (artist.Albums.Count == 0)
            for (var index = 1; index <= ArtistAlbumCount; index++)
                artist.Albums.Add(new AlbumCardViewModel(
                    70_000 + index, $"探针专辑 {index:D2}", CoverUrl(port, $"album-{index}")));

        artist.Name = "探针歌手";
        artist.HasAlbums = artist.Albums.Count > 0;
        artist.HasSingles = artist.Singles.Count > 0;
    }

    /// <summary>推荐页内容:每日 30 行 + 推荐歌单/热门歌曲/猜你喜欢 各 6 张卡片(封面走本地图床)。</summary>
    private static void FillRecommendContent(int port)
    {
        var recommend = ServiceLocator.Get<RecommendViewModel>();
        if (recommend.Sections.Count > 0) return;

        var player = ServiceLocator.Get<PlayerViewModel>();
        var songs = Enumerable.Range(1, 30).Select(index => new Song
        {
            Id = 900_000 + index,
            Source = MusicSource.NetEase,
            Name = $"每日推荐 {index:D2}",
            Artist = "探针歌手",
            ArtistIds = [6452],
            ArtistNames = ["探针歌手"],
            Album = "探针专辑",
            AlbumId = 70_000,
            DurationMs = 200_000,
            CoverUrl = CoverUrl(port, $"rec-{index}"),
        }).ToList();

        recommend.Sections.Add(new RecommendSectionViewModel(
            "每日歌曲推荐",
            songs.Select(song => (object)new SongItemViewModel(
                song, player.PlayFromList, queue: songs, source: "每日歌曲推荐")).ToList(),
            isBordered: true));

        AddCards("推荐歌单", "pl");
        AddCards("热门歌曲", "hot");
        AddCards("猜你喜欢", "new");

        void AddCards(string title, string key) => recommend.Sections.Add(
            new RecommendSectionViewModel(title, Enumerable.Range(1, 6)
                .Select(index => (object)new RecommendCardViewModel(
                    $"{title}{index}", "副标题", CoverUrl(port, $"{key}-{index}")))
                .ToList()));
    }

    /// <summary>每轮的歌手页封面 URL 带轮次后缀:模拟"每次进的是不同歌手",
    /// 避免同一 URL 反复命中缓存掩盖累积问题。
    /// 设 ALY_FIXED_COVER_URL=1 则去掉后缀,模拟"反复进同一个歌手"(真实点击路径:
    /// 同一歌手的封面 URL 恒定,磁盘字节缓存与解码 LRU 应当命中)。
    /// 两种口径要分开看:带后缀测的是"最坏情况每次都是新封面",不带后缀测的是"重复进同一页"。</summary>
    private static string CoverUrl(int port, string key)
    {
        var suffix = Environment.GetEnvironmentVariable("ALY_FIXED_COVER_URL") is { Length: > 0 }
            ? ""
            : $"-r{_round}";
        var url = $"http://127.0.0.1:{port}/cover/{key}{suffix}.png";
        ProbeCoverUrls.Add(url);
        return url;
    }

    private static void ReleaseProbeContent()
    {
        var artist = ServiceLocator.Get<ArtistViewModel>();
        artist.ReleaseCurrentPageData();
        artist.Songs.Clear();
        artist.Albums.Clear();
        artist.Singles.Clear();
        var recommend = ServiceLocator.Get<RecommendViewModel>();
        recommend.Sections.Clear();
    }

    /// <summary>删除探针写进统一媒体缓存的封面,避免污染真实用户缓存。</summary>
    private static void CleanupCoverCache()
    {
        try
        {
            var cache = ServiceLocator.Get<MusicCacheService>();
            foreach (var url in ProbeCoverUrls.Distinct())
                cache.RemoveCoverAsync(url).GetAwaiter().GetResult();
        }
        catch
        {
            // 清理失败不影响探针结论
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CaptureView<T>(AppShell shell, string label)
    {
        var view = shell.GetVisualDescendants().OfType<T>().FirstOrDefault();
        Assert(view is not null, $"导航后视觉树里找不到{label}");
        return new WeakReference(view);
    }

    private static void Collect()
    {
        for (var index = 0; index < 3; index++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    private static double Mb(long bytes) => bytes / 1024.0 / 1024.0;

    /// <summary>分阶段分配量的中位数(偶数个取中间两者的平均):比平均值抗单轮抖动。</summary>
    private static long Median(List<long> samples)
    {
        if (samples.Count == 0) return 0;
        var sorted = samples.OrderBy(value => value).ToList();
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    /// <summary>
    /// 探针本地图床:极简 HTTP/1.1 服务器,任意路径都回同一张 PNG(尺寸可控)。
    /// 不依赖外网与代理,保证封面管线走完整"HTTP → 编码字节缓存 → 解码"路径。
    /// </summary>
    private sealed class CoverServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly byte[] _png;
        private readonly CancellationTokenSource _cts = new();

        public CoverServer(int pixels)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _png = BuildPng(pixels);
            _ = AcceptLoopAsync();
        }

        public int Port { get; }

        /// <summary>累计收到的封面请求数(确认封面管线真的走到网络这一层)。</summary>
        public int RequestCount => Volatile.Read(ref _requestCount);

        private int _requestCount;

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false); }
                catch { return; }
                _ = ServeAsync(client);
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            try
            {
                using (client)
                {
                    var stream = client.GetStream();
                    var request = new StringBuilder();
                    var buffer = new byte[2048];
                    while (true)
                    {
                        var read = await stream.ReadAsync(buffer, _cts.Token).ConfigureAwait(false);
                        if (read <= 0) break;
                        request.Append(Encoding.ASCII.GetString(buffer, 0, read));
                        if (request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal)) break;
                    }

                    Interlocked.Increment(ref _requestCount);
                    var header = $"HTTP/1.1 200 OK\r\nContent-Type: image/png\r\n" +
                                 $"Content-Length: {_png.Length}\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(header), _cts.Token).ConfigureAwait(false);
                    await stream.WriteAsync(_png, _cts.Token).ConfigureAwait(false);
                    await stream.FlushAsync(_cts.Token).ConfigureAwait(false);
                }
            }
            catch
            {
                // 客户端提前关闭(页面切换取消了请求)属正常路径
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            _cts.Dispose();
        }

        /// <summary>生成尺寸可控的真 PNG(渐变色,压缩后微小,解码后 4 字节/像素)。</summary>
        private static byte[] BuildPng(int size)
        {
            var stride = size * 4 + 1;
            var raw = new byte[stride * size];
            for (var y = 0; y < size; y++)
            {
                var row = y * stride;
                raw[row] = 0; // filter: none
                for (var x = 0; x < size; x++)
                {
                    var p = row + 1 + x * 4;
                    raw[p] = (byte)(x * 255 / Math.Max(1, size - 1));
                    raw[p + 1] = (byte)(y * 255 / Math.Max(1, size - 1));
                    raw[p + 2] = 0x80;
                    raw[p + 3] = 0xFF;
                }
            }

            using var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
                zlib.Write(raw);

            using var png = new MemoryStream();
            png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

            var ihdr = new byte[13];
            WriteBigEndian(ihdr, 0, size);
            WriteBigEndian(ihdr, 4, size);
            ihdr[8] = 8;  // bit depth
            ihdr[9] = 6;  // color type: RGBA
            WriteChunk(png, "IHDR", ihdr);
            WriteChunk(png, "IDAT", compressed.ToArray());
            WriteChunk(png, "IEND", []);
            return png.ToArray();
        }

        private static void WriteBigEndian(byte[] target, int offset, int value)
        {
            target[offset] = (byte)(value >> 24);
            target[offset + 1] = (byte)(value >> 16);
            target[offset + 2] = (byte)(value >> 8);
            target[offset + 3] = (byte)value;
        }

        private static void WriteChunk(Stream stream, string type, byte[] data)
        {
            var length = new byte[4];
            WriteBigEndian(length, 0, data.Length);
            stream.Write(length);

            var payload = new byte[4 + data.Length];
            Encoding.ASCII.GetBytes(type).CopyTo(payload, 0);
            data.CopyTo(payload, 4);
            stream.Write(payload);

            var crc = new byte[4];
            WriteBigEndian(crc, 0, unchecked((int)Crc32(payload)));
            stream.Write(crc);
        }

        private static readonly uint[] CrcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                var c = n;
                for (var k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }

        private static uint Crc32(byte[] data)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var value in data)
                crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFu;
        }
    }
}
