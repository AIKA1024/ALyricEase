using ALyricEase.Infrastructure;
using ALyricEase.Services;
using ALyricEase.Services.Hotkeys;
using ALyricEase.ViewModels;
using ALyricEase.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Styling;
using Microsoft.Extensions.DependencyInjection;

namespace ALyricEase.Headless;

/// <summary>
/// 键盘快捷键回归(--hotkeys)。覆盖四层:
/// ① 手势文本的解析/显示/严格匹配;② 出厂默认键位互不冲突;
/// ③ 弹窗 VM 的改键/冲突拒绝/清除/恢复默认与 state.json 往返;
/// ④ 真实按键分发:窗口级命中、焦点在输入框时让位、模态弹层打开时让位,
///    以及弹窗视图在录音态用隧道阶段吞掉按键。
/// </summary>
internal static class HotkeyProbe
{
    public static int Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "aly-hotkeys-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var statePath = Path.Combine(root, "state.json");

        HeadlessApp.ConfigureServices(services =>
        {
            // 隔离真实用户配置:构造函数是 internal,Headless 有 InternalsVisibleTo
            services.AddSingleton(new AppStateStore(statePath));
            // 桌面歌词快捷键直接改这个 VM
            services.AddSingleton<DesktopLyricViewModel>();
            // 系统级热键对账器(注册器用下面的假实现,不碰真实 RegisterHotKey)
            services.AddSingleton<GlobalHotkeyHost>();
        });

        var globals = ServiceLocator.Get<GlobalHotkeyHost>();
        var registrar = new FakeGlobalHotkeyRegistrar();
        globals.Attach(registrar);

        var state = ServiceLocator.Get<AppStateStore>();
        var main = ServiceLocator.Get<MainViewModel>();
        var player = ServiceLocator.Get<PlayerViewModel>();
        Window? window = null;

        try
        {
            // ---- ① 手势文本 ----
            Check(HotkeyGesture.TryParse("Control+Shift+Left", out var seek), "解析 Control+Shift+Left");
            Check(seek.Key == Key.Left && seek.Modifiers == (KeyModifiers.Control | KeyModifiers.Shift), "解析结果正确");
            Check(seek.ToDisplayString() == "Ctrl + Shift + ←", $"显示格式:{seek.ToDisplayString()}");
            Check(seek.ToStorageString() == "Control+Shift+Left", "存储格式往返一致");
            // Win 不在支持范围:构造时剥掉(含 Win 的按键由 HotkeyService 直接丢弃,不参与匹配)
            Check(HotkeyGesture.Create(Key.Left, KeyModifiers.Control | KeyModifiers.Meta).Modifiers
                  == KeyModifiers.Control, "Win 修饰位被剥掉");
            Check(!seek.Matches(Key.Left, KeyModifiers.Control), "少按一个修饰键不算命中");
            Check(!HotkeyGesture.TryParse("123", out _), "数字串不是合法键名");
            Check(!HotkeyGesture.TryParse("Control+Left+Space", out _), "两个普通键的组合被拒绝");
            Check(!HotkeyGesture.TryParse("Tab", out _), "Tab 不可绑定");
            Check(!HotkeyGesture.TryParse("", out _), "空串不可绑定");
            foreach (var invalid in new[] { "Control++K", "Control+", "+K", "None+K", "A,B", "Escape", "Alt+F4", "Meta+K" })
                Check(!HotkeyGesture.TryParse(invalid, out _), $"拒绝无效或保留组合: {invalid}");
            Check(!seek.Matches(Key.Left, KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Meta),
                "多按 Win 不算命中");
            var damagedPath = Path.Combine(root, "damaged.json");
            File.WriteAllText(damagedPath, """{"Hotkeys":{"play-pause":"bad-key","volume-up":""}}""");
            var damaged = new AppStateStore(damagedPath);
            Check(damaged.ResolveHotkey(HotkeyCatalog.PlayPause) == HotkeyCatalog.GetDefault(HotkeyCatalog.PlayPause),
                "损坏键位回落默认");
            Check(damaged.ResolveHotkey(HotkeyCatalog.VolumeUp).IsEmpty, "显式清空不回落默认");

            // ---- ② 默认键位 ----
            var defaults = HotkeyCatalog.Actions.Select(a => a.Default.ToStorageString()).ToList();
            Check(defaults.All(d => d.Length > 0), "每个动作都有出厂默认键");
            Check(defaults.Count == defaults.Distinct(StringComparer.Ordinal).Count(), "出厂默认键位互不冲突");
            Check(state.Hotkeys.Count == 0, "全新配置没有任何覆盖项");

            // ---- ③ 弹窗 VM ----
            var dialog = main.Hotkeys;
            dialog.Reload();
            var playPause = dialog.Items.First(i => i.Definition.Id == HotkeyCatalog.PlayPause);
            var toggleNowPlaying = dialog.Items.First(i => i.Definition.Id == HotkeyCatalog.ToggleNowPlaying);
            Check(playPause.Gesture == HotkeyCatalog.GetDefault(HotkeyCatalog.PlayPause), "初始显示出厂默认");
            Check(!playPause.IsModified, "未改过的行不打点");

            dialog.BeginCapture(playPause);
            Check(dialog.IsCapturing && playPause.IsCapturing, "进入录音态");
            Check(dialog.HandleCaptureKey(Key.Up, KeyModifiers.Control), "录音态吞掉按键");
            Check(!string.IsNullOrEmpty(dialog.Hint), "冲突给出提示");
            Check(playPause.Gesture == HotkeyCatalog.GetDefault(HotkeyCatalog.PlayPause), "冲突时不改写");
            Check(dialog.IsCapturing, "冲突后保持录音态,允许直接再按");

            Check(dialog.HandleCaptureKey(Key.K, KeyModifiers.Control), "改键按键被吞");
            Check(playPause.Gesture == new HotkeyGesture(Key.K, KeyModifiers.Control), "改键成功");
            Check(playPause.IsModified && playPause.GestureText == "Ctrl + K", "行显示新组合并标记已改");
            Check(!dialog.IsCapturing, "改键后退出录音态");
            Check(state.ResolveHotkey(HotkeyCatalog.PlayPause) == playPause.Gesture, "新组合写入状态");

            // 落盘往返:重新打开一份 store 读同一个文件
            var reloaded = new AppStateStore(statePath);
            Check(reloaded.ResolveHotkey(HotkeyCatalog.PlayPause) == new HotkeyGesture(Key.K, KeyModifiers.Control),
                "改键已落盘");
            Check(reloaded.Hotkeys.TryGetValue(HotkeyCatalog.PlayPause, out var stored) && stored == "Control+K",
                "落盘格式是 Control+K");

            // 清除
            dialog.BeginCapture(playPause);
            dialog.HandleCaptureKey(Key.Back, KeyModifiers.None);
            Check(!playPause.HasBinding && playPause.GestureText == "未设置", "Backspace 清除绑定");
            Check(state.ResolveHotkey(HotkeyCatalog.PlayPause).IsEmpty, "清空后不响应");
            Check(new AppStateStore(statePath).ResolveHotkey(HotkeyCatalog.PlayPause).IsEmpty, "清空状态重启后保留");
            dialog.BeginCapture(playPause);
            dialog.HandleCaptureKey(Key.Back, KeyModifiers.Control);
            Check(playPause.Gesture == new HotkeyGesture(Key.Back, KeyModifiers.Control), "Ctrl+Backspace 可绑定");
            dialog.BeginCapture(playPause);
            dialog.HandleCaptureKey(Key.F4, KeyModifiers.Alt);
            Check(dialog.IsCapturing && playPause.Gesture.Key == Key.Back, "关闭窗口组合不能录入");
            dialog.HandleCaptureKey(Key.Escape, KeyModifiers.None);
            Check(!dialog.IsCapturing && playPause.Gesture.Key == Key.Back, "Esc 取消并保留原键位");

            // 恢复默认
            dialog.ResetAllCommand.Execute(null);
            Check(playPause.Gesture == HotkeyCatalog.GetDefault(HotkeyCatalog.PlayPause), "恢复默认键位");
            Check(state.Hotkeys.Count == 0, "恢复默认后配置无覆盖项");
            Check(dialog.Items.All(i => !i.IsModified), "全部行回到默认态");

            // ---- ④ 真实分发 ----
            // 先把配置清干净,用默认键位测
            state.ResetHotkeys();
            dialog.Reload();

            var service = new HotkeyService(state, () => main.IsAnyModalOpen);
            window = new Window
            {
                Width = 640,
                Height = 480,
                Content = new Border(),
            };
            window.AddHandler(InputElement.KeyDownEvent,
                (_, e) => service.TryHandle(window.FocusManager?.GetFocusedElement(), e),
                RoutingStrategies.Tunnel);
            window.Show();
            Drain();

            var volumeBefore = player.Volume;
            window.KeyPress(Key.Up, RawInputModifiers.Control, PhysicalKey.ArrowUp, null);
            Check(player.Volume == Math.Min(100, volumeBefore + HotkeyCatalog.VolumeStep),
                $"Ctrl+↑ 应加音量:{volumeBefore} → {player.Volume}");

            window.KeyPress(Key.Down, RawInputModifiers.Control, PhysicalKey.ArrowDown, null);
            Check(player.Volume == volumeBefore, "Ctrl+↓ 应减音量");

            // 焦点在输入框:同一个按键不再响应(用户在打字)
            var box = new TextBox();
            window.Content = box;
            box.Focus();
            Drain();
            var volumeInBox = player.Volume;
            window.KeyPress(Key.Up, RawInputModifiers.Control, PhysicalKey.ArrowUp, null);
            Check(player.Volume == volumeInBox, "文本框获得焦点时让位");

            // 模态弹层打开:后台不被误操作
            window.Content = new Border();
            main.IsHotkeyDialogOpen = true;
            Drain();
            var volumeInModal = player.Volume;
            window.KeyPress(Key.Up, RawInputModifiers.Control, PhysicalKey.ArrowUp, null);
            Check(player.Volume == volumeInModal, "模态弹层打开时不分发");
            main.IsHotkeyDialogOpen = false;
            main.IsErrorDialogOpen = true;
            window.KeyPress(Key.Up, RawInputModifiers.Control, PhysicalKey.ArrowUp, null);
            Check(player.Volume == volumeInModal, "错误提示弹窗也阻止快捷键");
            main.IsErrorDialogOpen = false;

            var volumeUp = dialog.Items.First(i => i.Definition.Id == HotkeyCatalog.VolumeUp);
            dialog.BeginCapture(volumeUp);
            dialog.HandleCaptureKey(Key.F6, KeyModifiers.None);
            window.KeyPress(Key.Up, RawInputModifiers.Control, PhysicalKey.ArrowUp, null);
            Check(player.Volume == volumeInModal, "改键后旧组合立即失效");
            window.KeyPress(Key.F6, RawInputModifiers.None, PhysicalKey.F6, null);
            Check(player.Volume == volumeInModal + HotkeyCatalog.VolumeStep, "改键后新组合立即生效");
            volumeUp.ClearCommand.Execute(null);
            window.KeyPress(Key.F6, RawInputModifiers.None, PhysicalKey.F6, null);
            Check(player.Volume == volumeInModal + HotkeyCatalog.VolumeStep, "清除后立即停止分发");
            dialog.ResetAllCommand.Execute(null);

            main.ShowNowPlaying = true;
            window.KeyPress(Key.F, RawInputModifiers.Control, PhysicalKey.F, null);
            Check(main.ActivePage == "Search" && !main.ShowNowPlaying, "从播放详情打开搜索时收起详情");

            // ---- ⑤ 桌面歌词动作 ----
            var lyrics = ServiceLocator.Get<DesktopLyricViewModel>();
            lyrics.Enabled = false;
            lyrics.ShowTranslation = false;
            const RawInputModifiers lyricMods = RawInputModifiers.Control | RawInputModifiers.Shift;

            window.KeyPress(Key.L, lyricMods, PhysicalKey.L, null);
            Check(lyrics.Enabled, "Ctrl+Shift+L 打开桌面歌词");
            window.KeyPress(Key.L, lyricMods, PhysicalKey.L, null);
            Check(!lyrics.Enabled, "再按一次关闭桌面歌词");

            window.KeyPress(Key.T, lyricMods, PhysicalKey.T, null);
            Check(lyrics.ShowTranslation, "Ctrl+Shift+T 显示翻译");
            window.KeyPress(Key.T, lyricMods, PhysicalKey.T, null);
            Check(!lyrics.ShowTranslation, "再按一次隐藏翻译");

            lyrics.Enabled = true;
            lyrics.ClickThrough = false;
            window.KeyPress(Key.P, lyricMods, PhysicalKey.P, null);
            Check(lyrics.ClickThrough, "Ctrl+Shift+P 开启鼠标穿透");
            window.KeyPress(Key.P, lyricMods, PhysicalKey.P, null);
            Check(!lyrics.ClickThrough, "再按一次解除穿透");

            lyrics.Enabled = false;
            window.KeyPress(Key.P, lyricMods, PhysicalKey.P, null);
            Check(lyrics.Enabled && lyrics.ClickThrough, "歌词未开启时按穿透会先显示歌词");
            lyrics.Enabled = false;
            Check(!lyrics.ClickThrough, "关闭桌面歌词顺带解除穿透");

            // ---- ⑥ 逐条「全局」热键 ----
            Check(globals.IsSupported, "接入模拟注册器后全局热键可用");

            // 单键规则:不产生字符的少数键才允许全局(空格/字母/Insert 会把键从别的程序手里抢走)。
            Check(GlobalHotkeyRules.CanRegister(new HotkeyGesture(Key.Pause, KeyModifiers.None)),
                "Pause 允许作为单键全局热键");
            Check(GlobalHotkeyRules.CanRegister(new HotkeyGesture(Key.MediaPlayPause, KeyModifiers.None)),
                "媒体键允许作为单键全局热键");
            Check(!GlobalHotkeyRules.CanRegister(new HotkeyGesture(Key.Space, KeyModifiers.None)),
                "空格不允许单键全局");
            Check(!GlobalHotkeyRules.CanRegister(new HotkeyGesture(Key.Insert, KeyModifiers.None)),
                "Insert 不允许单键全局");
            Check(GlobalHotkeyRules.CanRegister(new HotkeyGesture(Key.K, KeyModifiers.Control)),
                "带修饰键的组合允许全局");
            Check(HotkeyGesture.TryParse("Pause", out var pauseGesture)
                  && pauseGesture == new HotkeyGesture(Key.Pause, KeyModifiers.None),
                "Pause 能解析并落盘为单键");

            dialog.Reload();
            var playItem = dialog.Items.First(i => i.Definition.Id == HotkeyCatalog.PlayPause);
            var volumeItem = dialog.Items.First(i => i.Definition.Id == HotkeyCatalog.VolumeUp);
            var nextItem = dialog.Items.First(i => i.Definition.Id == HotkeyCatalog.NextTrack);

            playItem.IsGlobal = true;
            Check(!playItem.CanBeGlobal, "空格行不允许勾选全局（复选框禁用）");
            Check(!playItem.IsGlobal && !state.IsGlobalHotkey(HotkeyCatalog.PlayPause),
                "单键（空格）不能设为全局");
            Check(dialog.Hint is not null && dialog.Hint.Contains("Ctrl"), "单键被拒时给出原因");

            // 把播放/暂停改绑到 Pause 就能勾全局 —— 单键播放/暂停的正解。
            dialog.BeginCapture(playItem);
            dialog.HandleCaptureKey(Key.Pause, KeyModifiers.None);
            Check(playItem.Gesture == new HotkeyGesture(Key.Pause, KeyModifiers.None), "录音可录入 Pause");
            Check(playItem.CanBeGlobal, "改绑 Pause 后允许勾全局");
            playItem.IsGlobal = true;
            Check(registrar.Registered.TryGetValue(HotkeyCatalog.PlayPause, out var pauseRegistered)
                  && pauseRegistered == new HotkeyGesture(Key.Pause, KeyModifiers.None),
                "Pause 单键注册为全局");
            playItem.IsGlobal = false;

            volumeItem.IsGlobal = true;
            Check(volumeItem.IsGlobal && registrar.Registered.ContainsKey(HotkeyCatalog.VolumeUp),
                "勾选全局立即注册");
            Check(registrar.Registered[HotkeyCatalog.VolumeUp] == volumeItem.Gesture, "注册的是当前组合");

            registrar.Blocked.Add(HotkeyCatalog.NextTrack);
            nextItem.IsGlobal = true;
            Check(!nextItem.IsGlobal, "组合被占用时复选框回滚");
            Check(dialog.Hint is not null && dialog.Hint.Contains("占用"), "被占用时给出提示");
            registrar.Blocked.Remove(HotkeyCatalog.NextTrack);

            dialog.BeginCapture(volumeItem);
            dialog.HandleCaptureKey(Key.F7, KeyModifiers.Control);
            Check(registrar.Registered.TryGetValue(HotkeyCatalog.VolumeUp, out var rebound)
                  && rebound.Key == Key.F7, "改键后按新组合重新注册");
            Check(!registrar.Registered.ContainsKey(HotkeyCatalog.PlayPause), "未勾选的动作不会被注册");

            volumeItem.IsGlobal = false;
            Check(!registrar.Registered.ContainsKey(HotkeyCatalog.VolumeUp), "取消勾选立即注销");

            volumeItem.IsGlobal = true;
            volumeItem.ClearCommand.Execute(null);
            Check(!state.IsGlobalHotkey(HotkeyCatalog.VolumeUp)
                  && !registrar.Registered.ContainsKey(HotkeyCatalog.VolumeUp),
                "清空键位连带收回全局并注销");

            // 落盘 + 重启后仍是全局意图(实际注册由启动时的对账重做)
            dialog.BeginCapture(volumeItem);
            dialog.HandleCaptureKey(Key.F8, KeyModifiers.Control);
            volumeItem.IsGlobal = true;
            Check(volumeItem.IsGlobal, "清空后重新绑定可以再勾全局");
            state.Flush();
            var restarted = new AppStateStore(statePath);
            Check(restarted.IsGlobalHotkey(HotkeyCatalog.VolumeUp), "全局标记落盘");
            Check(dialog.Hint is not null && dialog.Hint.Contains("全局"), "勾选全局给出确认提示");

            dialog.ResetAllCommand.Execute(null);
            Check(registrar.Registered.Count == 0 && !state.IsGlobalHotkey(HotkeyCatalog.VolumeUp),
                "恢复默认释放全部全局热键");

            // 弹窗视图:录音态靠隧道阶段吞键,并真的改掉绑定
            var background = new Button { Content = "背景设置入口" };
            var view = new HotkeyDialogView { DataContext = main, IsVisible = false };
            using var visibleBinding = view.Bind(ModalVisibilityTransition.IsOpenProperty,
                new Binding(nameof(MainViewModel.IsHotkeyDialogOpen)));
            window.Content = new Grid { Children = { background, view } };
            background.Focus();
            main.Settings.EditHotkeysCommand.Execute(null);
            Drain();
            Check(main.IsHotkeyDialogOpen && view.IsVisualAncestorOf((Visual)window.FocusManager!.GetFocusedElement()!),
                "设置入口打开弹窗并移入焦点");
            for (var i = 0; i < 28; i++)
            {
                window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
                window.KeyRelease(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
                Check(view.IsVisualAncestorOf((Visual)window.FocusManager.GetFocusedElement()!), "Tab 不离开弹窗");
            }
            dialog.Reload();
            dialog.BeginCapture(toggleNowPlaying);
            Drain(); // 让 Post 的焦点转移生效
            window.KeyPress(Key.F2, RawInputModifiers.None, PhysicalKey.F2, null);
            Check(toggleNowPlaying.Gesture == new HotkeyGesture(Key.F2, KeyModifiers.None),
                $"视图录制 F2 失败,当前={toggleNowPlaying.GestureText}");
            Check(state.ResolveHotkey(HotkeyCatalog.ToggleNowPlaying).Key == Key.F2, "视图录制结果写入状态");
            dialog.BeginCapture(toggleNowPlaying);
            Drain();
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Check(main.IsHotkeyDialogOpen && !dialog.IsCapturing, "第一次 Esc 只取消录入，不关闭弹窗");
            Check(toggleNowPlaying.Gesture.Key == Key.F2, "取消录入保留已保存的键位");

            window.Width = 800;
            window.Height = 760;
            view.GetVisualDescendants().OfType<ScrollViewer>().First().Offset = default;
            Capture(window, root, "hotkeys-light.png");
            window.RequestedThemeVariant = ThemeVariant.Dark;
            Capture(window, root, "hotkeys-dark.png");
            window.Width = 640;
            window.Height = 480;
            Capture(window, root, "hotkeys-small.png");
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Check(!main.IsHotkeyDialogOpen, "第二次 Esc 关闭弹窗");
            var timeout = System.Diagnostics.Stopwatch.StartNew();
            while (view.IsVisible && timeout.Elapsed < TimeSpan.FromSeconds(3))
            {
                Drain();
                window.CaptureRenderedFrame()?.Dispose();
                Thread.Sleep(10);
            }
            Check(!view.IsVisible && ReferenceEquals(window.FocusManager.GetFocusedElement(), background),
                "关闭后焦点回到设置入口");

            Console.WriteLine($"[hotkeys] 全部通过；截图目录: {root}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[hotkeys] 失败: {ex.Message}");
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            window?.Close();
        }
    }

    private static void Capture(Window window, string root, string name)
    {
        // 等显隐动画走完，截图包含实际主题、字体与滚动区域。
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromMilliseconds(250))
        {
            Drain();
            window.CaptureRenderedFrame()?.Dispose();
            Thread.Sleep(10);
        }
        using var frame = window.CaptureRenderedFrame();
        using var output = File.Create(Path.Combine(root, name));
        frame!.Save(output, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }

    private static void Drain()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    /// <summary>模拟平台注册器:记录注册结果,可指定"被其他程序占用"的动作。</summary>
    private sealed class FakeGlobalHotkeyRegistrar : IGlobalHotkeyRegistrar
    {
        public HashSet<string> Blocked { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, HotkeyGesture> Registered { get; } = new(StringComparer.Ordinal);

        public bool TryRegister(string actionId, HotkeyGesture gesture)
        {
            if (Blocked.Contains(actionId)) return false;
            Registered[actionId] = gesture;
            return true;
        }

        public void Unregister(string actionId) => Registered.Remove(actionId);

        public void UnregisterAll() => Registered.Clear();
    }
}
