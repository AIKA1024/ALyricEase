using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media.Imaging; // TEMP-DIAG
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ALyricEase.Infrastructure;
using ALyricEase.ViewModels;
using ALyricEase.Views;

namespace ALyricEase.Headless;

/// <summary>响应式布局无头复现:不同窗口宽度下检查 PlayerBarView 的类应用与按钮可见性。</summary>
public static class Program
{
    private static Window? _window;

    [STAThread]
    public static void Main(string[] args)
    {
        // TEMP-DIAG:真窗口+真合成器变体(--lyricswitch-real):GDI 抓屏对比
        if (args.Length > 0 && args[0] == "--lyricswitch-real")
        {
            var realBuilder = AppBuilder.Configure<HeadlessApp>()
                .UsePlatformDetect();
            HeadlessApp.ConfigureServices();
            Dispatcher.UIThread.Post(async () =>
            {
                try { await LyricSwitchProbe.RunRealAsync(); }
                finally { Dispatcher.UIThread.InvokeShutdown(); }
            });
            realBuilder.StartWithClassicDesktopLifetime(Array.Empty<string>(), ShutdownMode.OnExplicitShutdown);
            return;
        }

        // 真窗口+真合成器版内存回归(--memloop-real):无头没有 GPU 纹理,封面内存必须真机验证
        if (args.Length > 0 && args[0] == "--memloop-real")
        {
            var realBuilder = AppBuilder.Configure<HeadlessApp>()
                .UsePlatformDetect();
            HeadlessApp.ConfigureServices();
            var exitCode = 1;
            Dispatcher.UIThread.Post(async () =>
            {
                try { exitCode = await PageLoopMemoryProbe.RunRealAsync(); }
                catch (Exception ex) { Console.Error.WriteLine($"[memloop] 异常: {ex}"); }
                finally { Dispatcher.UIThread.InvokeShutdown(); }
            });
            realBuilder.StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown);
            Environment.ExitCode = exitCode;
            return;
        }

        // 真窗口歌单页返回延迟(--pl-return-real):量 UI 冻结峰值与恢复各阶段耗时
        if (args.Length > 0 && args[0] == "--pl-return-real")
        {
            var realBuilder = AppBuilder.Configure<HeadlessApp>()
                .UsePlatformDetect();
            HeadlessApp.ConfigureServices();
            var exitCode = 1;
            Dispatcher.UIThread.Post(async () =>
            {
                try { exitCode = await PlaylistReturnLatencyProbe.RunRealAsync(); }
                catch (Exception ex) { Console.Error.WriteLine($"[pl-latency] 异常: {ex}"); }
                finally { Dispatcher.UIThread.InvokeShutdown(); }
            });
            realBuilder.StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown);
            Environment.ExitCode = exitCode;
            return;
        }

        AppBuilder.Configure<HeadlessApp>()
            .UseSkia()
            // 关闭 headless 假绘制:走 Skia 真渲染,RenderTargetBitmap 截图才有像素(默认 true 时 Save 出 0 字节 PNG)
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();

        HeadlessApp.ConfigureServices();

        // 单例歌单页重数据回归：离页归零，返回从一次性内存快照恢复内容/筛选/滚动状态。
        if (args.Length > 0 && args[0] == "--playlist-lifetime")
        {
            Environment.ExitCode = PlaylistPageLifetimeProbe.Run();
            return;
        }

        // 歌手/专辑及“查看全部”页面：离页释放，返回从一次性内存快照恢复。
        if (args.Length > 0 && args[0] == "--detail-lifetime")
        {
            Environment.ExitCode = DetailPageLifetimeProbe.Run();
            return;
        }

        // 个性推荐 ↔ 歌手页 往返循环：每轮往返后上一轮视图必须被回收，托管堆不得逐轮上涨。
        if (args.Length > 0 && args[0] == "--memloop")
        {
            Environment.ExitCode = PageLoopMemoryProbe.Run();
            return;
        }

        // 歌曲/专辑网格批量追加回归：同一 UI 周期只允许一次全量分块。
        if (args.Length > 0 && args[0] == "--grid-batching")
        {
            Environment.ExitCode = GridBatchingProbe.Run();
            return;
        }

        // 图片租约/LRU/取消与 SearchView 事件退订回归。
        if (args.Length > 0 && args[0] == "--image-lifetime")
        {
            Environment.ExitCode = ImageLifetimeProbe.Run();
            return;
        }

        // 歌词点击回归：鼠标按下不得先触发 ListBox 选中滚动，单次点击必须直接请求跳转。
        if (args.Length > 0 && args[0] == "--lyric-click")
        {
            Environment.ExitCode = LyricClickProbe.Run();
            return;
        }

        // 播放进度条回归：60 FPS 渲染推进、拖动 seek 与 hover 气泡必须使用同一视觉坐标。
        if (args.Length > 0 && args[0] == "--player-progress")
        {
            Environment.ExitCode = PlayerProgressProbe.Run();
            return;
        }

        // 封面 single-flight/LRU 回归：32 个同 URL 请求应只下载一次并共享同一解码实例。
        if (args.Length > 0 && args[0] == "--cover-cache")
        {
            Environment.ExitCode = System.Threading.Tasks.Task.Run(CoverLoaderProbe.RunAsync)
                .GetAwaiter().GetResult();
            return;
        }

        // 统一媒体缓存回归：音质升级/复用、封面、歌词翻译与清理。
        if (args.Length > 0 && args[0] == "--music-cache")
        {
            Environment.ExitCode = System.Threading.Tasks.Task.Run(MusicCacheProbe.RunAsync)
                .GetAwaiter().GetResult();
            return;
        }

        // 缓存读是否被 _mutationGate 堵住：强制裁剪占锁期间量各读路径耗时。
        if (args.Length > 0 && args[0] == "--cache-read-gate")
        {
            Environment.ExitCode = System.Threading.Tasks.Task.Run(CacheReadGateProbe.RunAsync)
                .GetAwaiter().GetResult();
            return;
        }

        // 最近播放回归：去重置顶、容量上限、元数据持久化与清空。
        if (args.Length > 0 && args[0] == "--recent-history")
        {
            Environment.ExitCode = RecentPlaybackProbe.Run();
            return;
        }

        // 集合排序/筛选回归：歌曲/专辑匹配、稳定排序、重置与响应式控件布局。
        if (args.Length > 0 && args[0] == "--collection-filter")
        {
            Environment.ExitCode = CollectionFilterProbe.Run();
            return;
        }

        // TEMP-DIAG:歌单接口"最后修改"时间字段实测(网易云/QQ 匿名公开歌单)
        if (args.Length > 0 && args[0] == "--pltime")
        {
            Environment.ExitCode = System.Threading.Tasks.Task.Run(PlaylistTimeProbe.RunAsync)
                .GetAwaiter().GetResult();
            return;
        }

        // 聚合歌单流式加载回归：来源稳定排序、100/300 批尺寸、范围集合单通知。
        if (args.Length > 0 && args[0] == "--aggregate-load")
        {
            Environment.ExitCode = AggregateLoadingProbe.Run();
            return;
        }

        // 懒歌单播放回归：完整随机范围、未显示歌曲按需解析、缓存有界。
        if (args.Length > 0 && args[0] == "--lazy-playback")
        {
            Environment.ExitCode = System.Threading.Tasks.Task.Run(LazyPlaybackQueueProbe.RunAsync)
                .GetAwaiter().GetResult();
            return;
        }

        if (args.Length > 0 && args[0] == "--probe")
        {
            PerfProbe.Run();
            return;
        }

        // 虚拟化换行网格探针:外部 ScrollViewer + 横向 VirtualizingStackPanel 是否真虚拟化
        if (args.Length > 0 && args[0] == "--vgrid")
        {
            VGridProbe.Run();
            return;
        }

        // SongGridView 实机探针:小屏滚到不足一列的末尾仍保持完整高度，且不在滚动中构造容器
        if (args.Length > 0 && args[0] == "--sg")
        {
            Environment.ExitCode = SongGridProbe.Run();
            return;
        }

        // 账号页登录态矩阵:未登录平台应有占位卡片 + 该平台登录按钮(不触网)
        if (args.Length > 0 && args[0] == "--accountstates")
        {
            AccountStatesProbe.Run();
            return;
        }

        // 换号后红心状态必须清空(不触网,用临时 cookie 文件,不碰真实登录态)
        if (args.Length > 0 && args[0] == "--accountlike")
        {
            Environment.ExitCode = AccountLikeStateProbe.Run();
            return;
        }

        // 专辑卡片几何探针:全部专辑页/歌手页卡片内封面与标题的实际矩形与间距
        if (args.Length > 0 && args[0] == "--albumgrid")
        {
            AlbumGridProbe.Run();
            return;
        }

        // 专辑详情页响应式探针:窄屏头部切换、标题宽度与按钮边界
        if (args.Length > 0 && args[0] == "--albumpage")
        {
            Environment.ExitCode = AlbumPageProbe.Run();
            return;
        }

        // 歌手卡片/推荐横向区块虚拟化探针:真实 ArtistView/RecommendView 懒加载改造验证
        if (args.Length > 0 && args[0] == "--acards")
        {
            ArtistCardsProbe.Run();
            return;
        }

        // QQ 音乐 API 冒烟:匿名打真实接口验证歌单修复(不依赖登录 Cookie)
        if (args.Length > 0 && args[0] == "--qqapi")
        {
            // 放线程池执行:STA Main + Avalonia SyncContext 下直接阻塞等待会与 await 延续互等(死锁)
            Environment.ExitCode = System.Threading.Tasks.Task.Run(QqApiProbe.RunAsync)
                .GetAwaiter().GetResult();
            return;
        }

        // QQ 音乐红心端到端实测(需本机有效 Cookie):AddSonglist→云端复检→DelSonglist 还原
        if (args.Length > 0 && args[0] == "--qqlike")
        {
            Environment.ExitCode = System.Threading.Tasks.Task.Run(QqApiProbe.RunLikeProbeAsync)
                .GetAwaiter().GetResult();
            return;
        }

        // 创建/删除歌单端到端实测(需本机有效 Cookie):两平台 Create→复检→Delete 还原
        if (args.Length > 0 && args[0] == "--createpl")
        {
            Environment.ExitCode = System.Threading.Tasks.Task.Run(CreatePlaylistProbe.RunAsync)
                .GetAwaiter().GetResult();
            return;
        }

        // QQ 扫码登录管线探针:QIMEI/会话/二维码/MQTT 全链路(不真实扫码)
        if (args.Length > 0 && args[0] == "--qqqr")
        {
            Environment.ExitCode = System.Threading.Tasks.Task.Run(QqQrLoginProbe.RunAsync)
                .GetAwaiter().GetResult();
            return;
        }

        // 网易云歌单加载计时:v6 概览 + 分批补页逐步耗时(复刻歌单页加载路径)
        if (args.Length > 0 && args[0] == "--nefill")
        {
            Environment.ExitCode = System.Threading.Tasks.Task.Run(NetEasePlaylistFillProbe.RunAsync)
                .GetAwaiter().GetResult();
            return;
        }

        // QQ 凭证过期诊断:同一份 Cookie 打 校验/账号摘要/红心写入 三条链路,打印真实错误码
        if (args.Length > 0 && args[0] == "--qqdiag")
        {
            Environment.ExitCode = System.Threading.Tasks.Task.Run(QqCredentialDiagProbe.RunAsync)
                .GetAwaiter().GetResult();
            return;
        }

        // QQ 音乐歌单重命名逆向:盲试 PlaylistBaseWrite 下的候选 method(建→改名→复检→删除还原)
        if (args.Length > 0 && args[0] == "--qqrename")
        {
            Environment.ExitCode = System.Threading.Tasks.Task.Run(QqRenameProbe.RunAsync)
                .GetAwaiter().GetResult();
            return;
        }

        // 重命名歌单端到端实测(需本机有效 Cookie):两平台 Create→Rename→复检→Delete 还原(生产代码路径)
        if (args.Length > 0 && args[0] == "--renamepl")
        {
            Environment.ExitCode = System.Threading.Tasks.Task.Run(RenamePlaylistProbe.RunAsync)
                .GetAwaiter().GetResult();
            return;
        }

        // 重命名弹窗渲染自查(不触网):预填 QQ 测试歌单打开弹窗,输出 PNG 供人工核对
        if (args.Length > 0 && args[0] == "--renamedlg")
        {
            RenameDialogProbe.Run();
            return;
        }

        // 菜单默认观感渲染自查(不触网):裸 MenuItem 填 MenuFlyoutPresenter,输出 PNG
        if (args.Length > 0 && args[0] == "--menustyle")
        {
            MenuStyleProbe.Run();
            return;
        }

        // 播放条歌曲菜单结构自查(不触网):菜单分组、多歌手子菜单、来源与公开链接
        if (args.Length > 0 && args[0] == "--playerbarmenu")
        {
            PlayerBarMenuProbe.Run();
            return;
        }

        // 菜单高度探针(不触网):实测各处菜单高度,核对入场偏移的"遮掉比例"
        if (args.Length > 0 && args[0] == "--menuheight")
        {
            MenuHeightProbe.Run();
            return;
        }

        // 菜单滑入动画自查(不触网):仿 WinUI3 打开动画,采样一级/子菜单 Transform 起点/终点与重放
        if (args.Length > 0 && args[0] == "--menuanim")
        {
            MenuAnimProbe.Run();
            return;
        }

        // 网易云加密通道诊断:定位 playlist/create 空响应成因(匿名注册晴雨表/明文写/weapi 原始响应)
        if (args.Length > 0 && args[0] == "--nediag")
        {
            Environment.ExitCode = System.Threading.Tasks.Task.Run(NetEaseDiagProbe.RunAsync)
                .GetAwaiter().GetResult();
            return;
        }

        // 官方客户端代理登录端到端:CopycatProxy MITM 捕获伪造 eapi 请求的 MUSIC_U(验证器注入,不碰存档)
        if (args.Length > 0 && args[0] == "--neproxy")
        {
            Environment.ExitCode = System.Threading.Tasks.Task.Run(NetEaseProxyLoginProbe.RunAsync)
                .GetAwaiter().GetResult();
            return;
        }

        // TEMP-DIAG:切歌后歌词容器残留旧词复现(A→B 时序/连切变体)
        if (args.Length > 0 && args[0] == "--lyricswitch")
        {
            LyricSwitchProbe.Run();
            return;
        }

        // TEMP-DIAG:正在播放覆盖层是否盖住标题栏
        if (args.Length > 0 && args[0] == "--npcover")
        {
            NpCoverProbe.Run(args.Length > 1 && args[1] == "anim");
            return;
        }

        // TEMP-DIAG:手机尺寸下正在播放覆盖层是否盖住 Android 顶部横幅
        if (args.Length > 0 && args[0] == "--npmobile")
        {
            NpMobileProbe.Run();
            return;
        }

        // TEMP-DIAG:搜索页导航行为验证(返回/重进/下钻三场景)
        if (args.Length > 0 && args[0] == "--searchnav")
        {
            SearchNavProbe.Run();
            return;
        }

        // TEMP-DIAG:搜索页改版渲染探针(落地页两态 + 结果页,输出临时 PNG)
        if (args.Length > 0 && args[0] == "--searchpage")
        {
            SearchPageProbe.Run();
            return;
        }

        // TEMP-DIAG:真实 MainWindow.axaml 解析验证
        if (args.Length > 0 && args[0] == "--realwin")
        {
            RealWindowProbe.Run();
            return;
        }

        // 登录弹窗三种 QQ 登录方式渲染自查（不触网、不发送验证码）。
        if (args.Length > 0 && args[0] == "--logindlg")
        {
            LoginDialogProbe.Run();
            return;
        }

        // Fluent 2 账号页双平台卡片渲染自查（使用内存预览数据，不读取真实凭证）。
        if (args.Length > 0 && args[0] == "--accountview")
        {
            AccountViewProbe.Run();
            return;
        }

        _window = new Window
        {
            Content = new AppShell { DataContext = ServiceLocator.Get<MainViewModel>() },
        };
        _window.Show();
        Dispatcher.UIThread.RunJobs();

        foreach (var width in new[] { 400.0, 640.0, 700.0, 1200.0 })
        {
            _window.Width = width;
            _window.Height = 800;
            Dispatcher.UIThread.RunJobs();
            Dispatcher.UIThread.RunJobs();
            Dump(width);
        }

        SyntheticTest();
        TapBackgroundTest();
    }

    /// <summary>窄屏下点播放条空白区(非按钮)应打开正在播放页。</summary>
    private static void TapBackgroundTest()
    {
        _window!.Width = 400;
        _window.Height = 800;
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();

        var bar = _window.GetVisualDescendants().OfType<PlayerBarView>().FirstOrDefault();
        var shellVm = _window.GetVisualDescendants().OfType<AppShell>().FirstOrDefault()?.DataContext as MainViewModel;
        if (bar is null || shellVm is null)
        {
            Console.WriteLine("[tap] 找不到 PlayerBarView/AppShell");
            return;
        }

        // 播放条内容行空白处(标题左侧与封面之间偏上,避开按钮/文本也可命中:Grid 有透明背景)
        var point = bar.TranslatePoint(new Point(200, 45), _window);
        if (point is null)
        {
            Console.WriteLine("[tap] 坐标换算失败");
            return;
        }

        _window.MouseMove(point.Value);
        _window.MouseDown(point.Value, Avalonia.Input.MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        _window.MouseUp(point.Value, Avalonia.Input.MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Console.WriteLine($"[tap] 点击播放条空白区后 ShowNowPlaying={shellVm.ShowNowPlaying} (期望 True)");

        DragProgressTest(bar, shellVm);
    }

    /// <summary>窄屏下拖动紧凑进度条:球和 ScrubPositionMs 应跟随。</summary>
    private static void DragProgressTest(PlayerBarView bar, MainViewModel shellVm)
    {
        shellVm.CloseNowPlayingCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var vm = (bar.DataContext as ViewModels.PlayerViewModel)!;
        var point = bar.TranslatePoint(new Point(100, 2), _window!)!.Value;
        _window!.MouseMove(point);
        _window.MouseDown(point, Avalonia.Input.MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        var moved = bar.TranslatePoint(new Point(300, 2), _window)!.Value;
        _window.MouseMove(moved);
        Dispatcher.UIThread.RunJobs();
        DumpAndShot(bar, "CompactPlayer", "aly-narrow-drag"); // TEMP-DIAG
        _window.MouseUp(moved, Avalonia.Input.MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        // 按父容器区分宽/紧凑实例:父级隐藏时子元素自身 IsVisible 仍为 true,不能用它筛
        var progress = bar.GetVisualDescendants().OfType<PlayerProgressBar>()
            .FirstOrDefault(p => p.GetVisualParent() is Grid { Name: "CompactPlayer" });
        var thumb = progress?.FindControl<Grid>("Thumb");
        var bubble = progress?.FindControl<Border>("Bubble");
        Console.WriteLine($"[drag] 拖到 300/400 处: ScrubPositionMs={vm.ScrubPositionMs:F0} (期望≈135000) " +
            $"球Margin.Left={thumb?.Margin.Left:F0} (期望≈290) 气泡可见={bubble?.IsVisible} (期望 True)");
        var fill = progress?.FindControl<Ellipse>("ThumbFill");
        Console.WriteLine($"[ball] 静止内球大小: {fill?.Width:F0}x{fill?.Height:F0} (期望 10x10)");

        WideBubbleTest();
    }

    /// <summary>宽屏下拖动进度条时应显示时间气泡。</summary>
    private static void WideBubbleTest()
    {
        _window!.Width = 1200;
        _window.Height = 800;
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();

        var bar = _window.GetVisualDescendants().OfType<PlayerBarView>().FirstOrDefault();
        var shellVm = _window.GetVisualDescendants().OfType<AppShell>().FirstOrDefault()?.DataContext as MainViewModel;
        if (bar is null || shellVm is null) return;

        var progress = bar.GetVisualDescendants().OfType<PlayerProgressBar>()
            .FirstOrDefault(p => p.GetVisualParent() is Grid { Name: "WidePlayer" });
        if (progress is null)
        {
            Console.WriteLine("[bubble] 找不到宽屏进度条");
            return;
        }

        var point = bar.TranslatePoint(new Point(600, 0), _window)!.Value;
        _window.MouseMove(point);
        _window.MouseDown(point, Avalonia.Input.MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        DumpAndShot(bar, "WidePlayer", "aly-wide-drag"); // TEMP-DIAG

        var bubble = progress.FindControl<Border>("Bubble");
        Console.WriteLine($"[bubble] 拖动中 Bubble.IsVisible={bubble?.IsVisible} margin=({bubble?.Margin.Left:F0},{bubble?.Margin.Top:F0}) (期望 True)");

        _window.MouseUp(point, Avalonia.Input.MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Console.WriteLine($"[bubble] 松开后 Bubble.IsVisible={bubble?.IsVisible} (期望 False)");
    }

    /// <summary>TEMP-DIAG:打印指定播放条实例中气泡状态并渲染窗口截图。</summary>
    private static void DumpAndShot(PlayerBarView bar, string parentGrid, string name)
    {
        var prog = bar.GetVisualDescendants().OfType<PlayerProgressBar>()
            .FirstOrDefault(p => p.GetVisualParent() is Grid g && g.Name == parentGrid);
        var bub = prog?.FindControl<Border>("Bubble");
        var thumb = prog?.FindControl<Grid>("Thumb");
        Console.WriteLine($"[diag:{name}] prog={prog?.Bounds} thumb.Margin=({thumb?.Margin.Left:F0},{thumb?.Margin.Top:F0}) " +
            $"bubble.IsVisible={bub?.IsVisible} bubble.Margin=({bub?.Margin.Left:F0},{bub?.Margin.Top:F0}) bubble.Bounds={bub?.Bounds}");
        if (bub is { IsVisible: true } && prog is not null)
        {
            var tl = bub.TranslatePoint(new Point(0, 0), _window!);
            Console.WriteLine($"[diag:{name}] bubble 窗口坐标 TL={tl}");
        }
        var px = new PixelSize((int)_window!.ClientSize.Width, (int)_window!.ClientSize.Height);
        using var rtb = new RenderTargetBitmap(px, new Vector(96, 96));
        rtb.Render(_window);
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), name + ".png");
        rtb.Save(path);
        Console.WriteLine($"[diag:{name}] 截图: {path}");
    }

    /// <summary>合成用例:UserControl.Styles 里 "UserControl.class 后代" 选择器是否能激活。</summary>
    private static void SyntheticTest()
    {
        var host = new SyHost();
        var grid = new Grid { Name = "SynGrid" };
        host.Content = grid;

        // 与 PlayerBarView 相同的结构:基样式设 False,带 UserControl 前缀的样式设 True
        host.Styles.Add(new Style(s => s.OfType<Grid>().Name("SynGrid"))
        {
            Setters = { new Setter(Visual.IsVisibleProperty, false) },
        });
        host.Styles.Add(new Style(s => s.OfType<UserControl>().Class("sy").Descendant().OfType<Grid>().Name("SynGrid"))
        {
            Setters = { new Setter(Visual.IsVisibleProperty, true) },
        });

        var win = new Window { Content = host, Width = 300, Height = 200 };
        win.Show();
        Dispatcher.UIThread.RunJobs();
        Console.WriteLine($"[synthetic] 未加类: grid.IsVisible={grid.IsVisible} (期望 False)");

        host.Classes.Add("sy");
        Dispatcher.UIThread.RunJobs();
        Console.WriteLine($"[synthetic] 加类后: grid.IsVisible={grid.IsVisible} (期望 True; 若为 False 则前缀选择器失效)");

        // 变体A:去掉类型名,只用 .sy 类选择器做前缀
        var hostA = new SyHost();
        var gridA = new Grid { Name = "SynGridA" };
        hostA.Content = gridA;
        hostA.Styles.Add(new Style(s => s.Class("sy").Descendant().OfType<Grid>().Name("SynGridA"))
        {
            Setters = { new Setter(Visual.IsVisibleProperty, true) },
        });
        var winA = new Window { Content = hostA, Width = 300, Height = 200 };
        winA.Show();
        Dispatcher.UIThread.RunJobs();
        hostA.Classes.Add("sy");
        Dispatcher.UIThread.RunJobs();
        Console.WriteLine($"[synthetic] 变体A(.sy 前缀): gridA.IsVisible={gridA.IsVisible} (期望 True)");

        // 变体B:类放在 UserControl 内部的中间层 Grid 上,前缀匹配该中间层(非样式宿主)
        var hostB = new SyHost();
        var middle = new Grid { Name = "Middle" };
        var gridB = new Grid { Name = "SynGridB" };
        middle.Children.Add(gridB);
        hostB.Content = middle;
        hostB.Styles.Add(new Style(s => s.OfType<Grid>().Name("Middle").Class("sy").Descendant().OfType<Grid>().Name("SynGridB"))
        {
            Setters = { new Setter(Visual.IsVisibleProperty, true) },
        });
        var winB = new Window { Content = hostB, Width = 300, Height = 200 };
        winB.Show();
        Dispatcher.UIThread.RunJobs();
        middle.Classes.Add("sy");
        Dispatcher.UIThread.RunJobs();
        Console.WriteLine($"[synthetic] 变体B(内部中间层带类): gridB.IsVisible={gridB.IsVisible} (期望 True)");
    }

    private sealed class SyHost : UserControl
    {
    }

    private static void Dump(double width)
    {
        var bar = _window?.GetVisualDescendants().OfType<PlayerBarView>().FirstOrDefault();

        // 模拟有歌曲播放:让进度区可见,验证紧凑条进度球位置计算
        var vm = bar?.DataContext as ViewModels.PlayerViewModel;
        if (vm is { CurrentSong: null })
        {
            vm.CurrentSong = new Models.Song { Id = 1, Name = "测试歌曲", DurationMs = 180_000 };
            vm.DurationMs = 180_000;
            vm.PositionMs = 60_000;
            Dispatcher.UIThread.RunJobs();
        }
        if (bar is null)
        {
            Console.WriteLine($"[{width}] PlayerBarView NOT FOUND");
            return;
        }

        var classes = string.Join(",", new[] { "wide", "compact", "narrow" }.Where(c => bar.Classes.Contains(c)));
        var wide = bar.FindControl<Grid>("WidePlayer");
        var compact = bar.FindControl<Grid>("CompactPlayer");

        Console.WriteLine($"--- width={width} window={_window!.ClientSize.Width:F0} bounds={bar.Bounds.Width:F0} classes=[{classes}]");
        Console.WriteLine($"    WidePlayer.IsVisible={wide?.IsVisible} CompactPlayer.IsVisible={compact?.IsVisible}");

        if (wide is { IsVisible: true })
            Console.WriteLine($"    Wide 按钮数: {CountButtons(wide)}");
        if (compact is { IsVisible: true })
        {
            Console.WriteLine($"    Compact 按钮数: {CountButtons(compact)} bar高={bar.Bounds.Height:F0}");
            var progress = compact.GetVisualDescendants().OfType<PlayerProgressBar>()
                .FirstOrDefault(p => p.IsVisible);
            var thumb = progress?.FindControl<Grid>("Thumb");
            var track = progress?.FindControl<Grid>("Track");
            if (thumb is not null && track is not null)
                Console.WriteLine($"    紧凑进度球: track宽={track.Bounds.Width:F0} 球Margin.Left={thumb.Margin.Left:F0} 球可见={thumb.IsVisible}");

            // 无真实歌曲时强制显示按钮组和图标,检查布局与字形
            var panel = compact.GetVisualDescendants().OfType<StackPanel>()
                .FirstOrDefault(p => p.Orientation == Orientation.Horizontal);
            if (panel is not null)
            {
                panel.IsVisible = true;
                Dispatcher.UIThread.RunJobs();
                foreach (var b in panel.Children.OfType<Button>())
                {
                    var icons = b.GetVisualDescendants().OfType<TextBlock>()
                        .Where(t => t.Text?.Length > 0).ToList();
                    foreach (var icon in icons) icon.IsVisible = true;
                }
                Dispatcher.UIThread.RunJobs();
                foreach (var b in panel.Children.OfType<Button>())
                {
                    var icon = b.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Text?.Length > 0);
                    Console.WriteLine($"    按钮 {b.Bounds.Width:F0}x{b.Bounds.Height:F0} 图标='{Describe(icon?.Text)}' 图标bounds={icon?.Bounds.Width:F0}x{icon?.Bounds.Height:F0}");
                }
            }
        }
    }

    private static string Describe(string? text) =>
        text is null ? "(无)"
            : text.All(c => c > 0x7F) && text.Length > 0 ? $"U+{(int)text[0]:X4}"
            : text;

    private static int CountButtons(Grid root) =>
        root.GetVisualDescendants().OfType<Button>().Count(b => b.IsVisible);
}
