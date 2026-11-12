using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.ViewModels;
using ALyricEase.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;

namespace ALyricEase.Headless;

internal static class DesktopLyricProbe
{
    public static void Run()
    {
        // 贴底保存的位置必须原样还原:曾经用固定 180 当"估计高度",把贴底的位置顶到 Bottom-180。
        Check(DesktopLyricPlacement.ClampTop(922, 118, 0, 1040) == 922, "贴底位置原样还原");
        Check(DesktopLyricPlacement.ClampTop(1000, 118, 0, 1040) == 922, "底部超出时只上推到贴底");
        Check(DesktopLyricPlacement.ClampTop(-50, 118, 0, 1040) == 0, "顶部超出时下拉回工作区");
        Check(DesktopLyricPlacement.ClampTop(300, 0, 0, 1040) == 300, "高度未知时不改变合法位置");
        // 首帧前 SizeChanged 会报 (Width, Width) 的假尺寸,必须拒掉(否则 1040-720=320)。
        Check(!DesktopLyricPlacement.IsPlausibleHeight(720, 1040), "假尺寸（宽度当高度）被拒");
        Check(DesktopLyricPlacement.IsPlausibleHeight(100, 1040), "真实窗口高度可用");
        Check(!DesktopLyricPlacement.IsPlausibleHeight(0, 1040), "高度为 0 时跳过");

        var dir = Path.Combine(Path.GetTempPath(), "aly-desktop-lyrics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var state = new AppStateStore(Path.Combine(dir, "state.json"));
        HeadlessApp.ConfigureServices(services =>
        {
            services.AddSingleton(state);
            services.AddSingleton<DesktopLyricViewModel>();
            services.AddSingleton(new MusicCacheService(64, Path.Combine(dir, "cache"), new HttpClient()));
        });
        var player = ServiceLocator.Get<PlayerViewModel>();
        var lyric = ServiceLocator.Get<LyricViewModel>();
        using var model = ServiceLocator.Get<DesktopLyricViewModel>();
        var path = Path.Combine(dir, "test.mp3");
        File.WriteAllText(Path.ChangeExtension(path, ".lrc"), "[offset:500]\n[00:01.00]第一句\n[00:03.00]第二句");
        var song = new Song { Name = "本地测试", Artist = "歌手", Source = MusicSource.Local, LocalFilePath = path };
        player.CurrentSong = song;
        var load = lyric.LoadAsync(song);
        lyric.UpdatePosition(2800);
        PumpUntil(() => load.IsCompleted && lyric.HasLyric);
        Check(model.Original == "第二句", "加载完成立即定位进度与 offset");
        lyric.UpdatePosition(600);
        Check(model.Original == "第一句", "向后跳转同步");
        lyric.Lines[0].Translation = "Translation";
        model.ShowTranslation = false;
        model.ShowTranslation = true;
        Check(model.Translation == "Translation", "翻译显示");
        model.ShowTranslation = false;
        Check(!model.HasTranslation, "隐藏翻译");
        player.CurrentSong = new Song { Name = "新歌曲" };
        Check(model.Original == "新歌曲", "切歌立即清除旧词");
        model.Enabled = true;
        model.FontSize = 40;
        model.BackgroundOpacity = 0.25;
        model.ClickThrough = true;
        state.Flush();
        var restored = new AppStateStore(Path.Combine(dir, "state.json"));
        Check(restored.DesktopLyricEnabled && restored.DesktopLyricFontSize == 40
            && restored.DesktopLyricBackgroundOpacity == 0.25 && restored.DesktopLyricClickThrough,
            "偏好持久化（含穿透）");

        // 暂停时隐藏歌词:暂停/停止收起,继续播放恢复;换歌的 Loading 不能算暂停(否则每切一首闪一下)。
        model.HideWhenPaused = true;
        Check(!model.IsWindowVisible, "暂停时隐藏歌词窗口");
        player.IsPlaying = true;
        Check(model.IsWindowVisible, "继续播放后歌词重新显示");
        player.IsPlaying = false;
        player.IsLoading = true;
        Check(model.IsWindowVisible, "换歌加载中不闪");
        player.IsLoading = false;
        Check(!model.IsWindowVisible, "加载结束仍暂停时重新收起");
        state.Flush();
        Check(new AppStateStore(Path.Combine(dir, "state.json")).DesktopLyricHideWhenPaused,
            "暂停隐藏开关落盘");
        model.HideWhenPaused = false;
        Check(model.IsWindowVisible, "关掉开关立即恢复显示");

        // ---- 控制器层:暂停时真的收起窗口,继续播放原地回来(位置不重算) ----
        // 放在窗口交互断言之前:后面那条"穿透收起工具栏"是历史遗留的红灯,会中断后面的所有断言。
        model.Enabled = false;
        model.ClickThrough = false;
        using (var controller = new DesktopLyricController(model, state))
        {
            model.Enabled = true;
            Dispatcher.UIThread.RunJobs();
            var lyricWindow = controller.LyricWindow ?? throw new InvalidOperationException("未创建歌词窗口");
            Check(lyricWindow.IsVisible, "开启歌词后窗口显示");
            var resting = lyricWindow.Position;
            model.HideWhenPaused = true;
            Dispatcher.UIThread.RunJobs();
            Check(!lyricWindow.IsVisible, "暂停时窗口真的收起（不只是透明）");
            player.IsPlaying = true;
            Dispatcher.UIThread.RunJobs();
            Check(lyricWindow.IsVisible && lyricWindow.Position == resting, "继续播放后原地恢复");
            player.IsPlaying = false;
            Dispatcher.UIThread.RunJobs();
            Check(!lyricWindow.IsVisible, "再次暂停重新收起");
        }
        Check(model.Enabled, "收起窗口不改写歌词总开关");
        // 还原状态给后续断言（「关闭歌词解除穿透」需要先处于穿透状态）。
        model.HideWhenPaused = false;
        model.Enabled = true;
        model.ClickThrough = true;

        // 关闭歌词要连带收回穿透,且不能把 true 残留在配置里(否则下次打开一上来就点不动)。
        model.Enabled = false;
        Check(!model.ClickThrough, "关闭歌词解除穿透");
        Check(!new AppStateStore(Path.Combine(dir, "state.json")).DesktopLyricClickThrough,
            "穿透复位已落盘");
        model.Enabled = true; // 恢复,后面的窗口交互测试需要窗口可见
        var window = new DesktopLyricWindow { DataContext = model };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Check(window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "新歌曲"), "窗口编译绑定");
        // 顶部按钮行已去掉:窗口里不该再有任何按钮,整块歌词区域自己就是拖动热区。
        Check(!window.GetVisualDescendants().OfType<Button>().Any(), "歌词窗口没有按钮行");
        var lyricSurface = window.FindControl<Border>("LyricSurface")!;
        var surfacePoint = lyricSurface.TranslatePoint(
            new Avalonia.Point(lyricSurface.Bounds.Width / 2, lyricSurface.Bounds.Height / 2), window)!.Value;
        Check(lyricSurface.Bounds.Height > 0 && window.InputHitTest(surfacePoint) is not null,
            "歌词区域可接收拖动输入");
        // 翻译行跟着设置走(原来靠工具栏那个「译」按钮验证,按钮没了直接看文本控件)。
        var lyricRows = window.GetVisualDescendants().OfType<ALyricEase.Controls.DesktopLyricText>().ToArray();
        model.ShowTranslation = true;   // 先让 RefreshText 跑一遍,再手动塞翻译
        model.Translation = "翻译内容";
        Dispatcher.UIThread.RunJobs();
        Check(lyricRows.Length == 2 && lyricRows[1].IsVisible, "翻译行受开关控制（开）");
        model.ShowTranslation = false;
        Dispatcher.UIThread.RunJobs();
        Check(!lyricRows[1].IsVisible, "翻译行受开关控制（关）");
        model.ShowTranslation = true;
        model.Translation = "翻译内容";
        Dispatcher.UIThread.RunJobs();
        var content = (Control)window.Content!;
        using (var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(
            new Avalonia.PixelSize((int)Math.Ceiling(content.Bounds.Width), (int)Math.Ceiling(content.Bounds.Height))))
        {
            bitmap.Render(content);
            bitmap.Save(Path.Combine(Path.GetTempPath(), "aly-desktop-lyrics-preview.png"), PngBitmapEncoderOptions.Default);
        }
        var lyricText = window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "新歌曲");
        var lyricOrigin = lyricText.PointToScreen(default);
        var expandedPosition = window.ExpandedPosition;
        var windowSize = window.ClientSize;
        model.ClickThrough = true;
        Dispatcher.UIThread.RunJobs();
        Check(lyricText.PointToScreen(default) == lyricOrigin && window.ClientSize == windowSize
            && window.ExpandedPosition == expandedPosition,
            "开启穿透不改变歌词位置与窗口尺寸");
        model.ClickThrough = false;
        Dispatcher.UIThread.RunJobs();
        Check(lyricText.PointToScreen(default) == lyricOrigin && window.ClientSize == windowSize
            && window.ExpandedPosition == expandedPosition,
            "解除穿透恢复窗口，歌词屏幕位置与保存位置不变");
        for (var i = 0; i < 8; i++)
        {
            model.ClickThrough = true;
            Dispatcher.UIThread.RunJobs();
            model.ClickThrough = false;
            Dispatcher.UIThread.RunJobs();
        }
        Check(lyricText.PointToScreen(default) == lyricOrigin && window.ExpandedPosition == expandedPosition,
            "反复切换穿透不累积位移");
        model.ClickThrough = true;
        model.CloseCommand.Execute(null);
        Check(!model.Enabled && !model.ClickThrough, "关闭同时解除穿透");
        Check(!new AppStateStore(Path.Combine(dir, "state.json")).DesktopLyricClickThrough,
            "关闭歌词后穿透不残留在配置里");
        window.Close();
        var main = ServiceLocator.Get<MainViewModel>();
        main.Settings.OpenDesktopLyricDialogCommand.Execute(null);
        Check(main.IsDesktopLyricDialogOpen && main.IsAnyModalOpen, "歌词关闭时仍可打开设置模态框");
        var dialog = new DesktopLyricSettingsDialogView { DataContext = main };
        Check(main.Settings.DesktopLyrics!.FontOptions.Count > 1
            && main.Settings.DesktopLyrics.FontOptions[0].FamilyName is null, "字体列表含系统字体且首项为默认");
        var dialogWindow = new Window { Width = 360, Height = 620, Content = dialog };
        dialogWindow.Show();
        Dispatcher.UIThread.RunJobs();
        // 新弹窗按"常规 / 文字 / 效果"分段,一次只渲染一段 ⇒ 可见滑块只有一个(背景不透明度)。
        var visibleSliders = dialog.GetVisualDescendants().OfType<Slider>()
            .Where(s => s.Bounds.Width > 0).ToArray();
        Check(visibleSliders.Length == 1 && visibleSliders[0].Bounds.Width < 360, "窄屏滑块布局（默认段只露一个滑块）");
        Check(dialog.GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("tab")) == 3, "分段标签三段");
        // 切换分段时标签的几何必须纹丝不动:选中态曾多给 1px 描边,那个按钮就比别家宽 2px,
        // 点谁谁变宽、其余按钮横跳(实测 64/62/62)。
        var tabButtons = new[] { "TabGeneral", "TabText", "TabEffects" }
            .Select(name => dialog.GetControl<Button>(name)).ToArray();
        var tabGeometry = tabButtons.Select(b => (b.Bounds.X, b.Bounds.Width)).ToArray();
        // 弹窗外壳高度必须固定:分段内容高矮差很多(常规/文字/效果 ≈ 668/700/348),
        // 内容自适应的话点一次标签整张卡片就抽一下 —— 现在按"可用高度夹到 700"写死。
        var dialogCard = dialog.GetControl<Border>("DialogCard");
        var cardHeight = dialogCard.Bounds.Height;
        // Height 必须是**显式值**:只比较 Bounds 相等会假阳性(Grid 的 `*` 行在矮窗口里
        // 也能把卡片撑成同一个高度,看着"没变"其实代码压根没生效 —— 曾经漏了 NaN 判断)。
        Check(double.IsFinite(dialogCard.Height) && Math.Abs(dialogCard.Height - cardHeight) < 0.5,
            "弹窗高度是显式算好的,不是内容自适应");
        Check(Math.Abs(dialogCard.Height - (dialogWindow.Height - 32)) < 1, "弹窗高度=可用高度减去外边距");
        Check(cardHeight > 0 && cardHeight <= dialogWindow.Height, "弹窗高度不超过可用空间");
        foreach (var tab in tabButtons)
        {
            tab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Check(tabButtons.Select(b => (b.Bounds.X, b.Bounds.Width)).SequenceEqual(tabGeometry),
                $"切换分段不改变标签位置与宽度（{tab.Content}）");
            Check(Math.Abs(dialogCard.Bounds.Height - cardHeight) < 0.5,
                $"切换分段不改变弹窗高度（{tab.Content}）");
        }
        // 窗口变矮时外壳跟着压,但依旧与当前分段无关。
        dialogWindow.Height = 480;
        Dispatcher.UIThread.RunJobs();
        var shortCardHeight = dialogCard.Bounds.Height;
        Check(shortCardHeight > 0 && shortCardHeight < cardHeight - 100, "窗口变矮时弹窗压到可用高度");
        Check(Math.Abs(dialogCard.Height - (480 - 32)) < 1, "矮窗口下高度重算为新的可用高度");
        foreach (var tab in tabButtons)
        {
            tab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Check(Math.Abs(dialogCard.Bounds.Height - shortCardHeight) < 0.5,
                $"矮窗口下切换分段也不改变弹窗高度（{tab.Content}）");
        }
        dialogWindow.Height = 620;
        Dispatcher.UIThread.RunJobs();

        // 真实宿主不是"直接当窗口内容",而是 MainWindowShell 的 `RowDefinitions="48,*"` 网格(弹窗跨两行)。
        // 这里复刻一遍:确认 Bounds.Height 拿到的是整窗高度、并且夹到 700 上限后三段都不动。
        var shellWindow = new Window { Width = 1200, Height = 800 };
        var shellGrid = new Grid { RowDefinitions = new RowDefinitions("48,*") };
        var shellDialog = new DesktopLyricSettingsDialogView { DataContext = main };
        Grid.SetRow(shellDialog, 0);
        Grid.SetRowSpan(shellDialog, 2);
        shellGrid.Children.Add(shellDialog);
        shellWindow.Content = shellGrid;
        shellWindow.Show();
        Dispatcher.UIThread.RunJobs();
        var shellCard = shellDialog.GetControl<Border>("DialogCard");
        Check(Math.Abs(shellCard.Height - 700) < 1, "真实宿主（跨两行网格）下高度取上限 700");
        foreach (var tab in new[] { "TabGeneral", "TabText", "TabEffects" })
        {
            shellDialog.GetControl<Button>(tab).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Check(Math.Abs(shellCard.Height - 700) < 1, $"真实宿主下切换分段不改高度（{tab}）");
        }
        shellWindow.Close();
        var textTab = dialog.GetControl<Button>("TabText");
        // 三个段共用一个滚动容器,切段后必须回到顶部(否则在"常规"滚到底再切"效果"会落在半截上)。
        var contentScroll = dialog.GetControl<ScrollViewer>("ContentScroll");
        dialog.GetControl<Button>("TabEffects").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        contentScroll.Offset = new Vector(0, contentScroll.Extent.Height);
        textTab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Check(contentScroll.Offset.Y == 0, "切换分段后滚回顶部");
        textTab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        var fontSlider = dialog.GetVisualDescendants().OfType<Slider>()
            .First(s => s.Bounds.Width > 0 && Avalonia.Automation.AutomationProperties.GetName(s) == "歌词字号");
        fontSlider.Value = 32;
        Check(model.FontSize == 32, "弹窗调整即时同步");
        // 预览必须 1:1 跟着字号走(曾经封顶 26,26 以上拖滑杆预览不动),板子则跟着长高。
        var previewText = dialog.GetVisualDescendants().OfType<ALyricEase.Controls.DesktopLyricText>().First();
        fontSlider.Value = 48;
        Dispatcher.UIThread.RunJobs();
        Check(Math.Abs(previewText.FontSize - 48) < 0.01, "预览字号跟随真实字号（不封顶）");
        var previewPlate = dialog.GetVisualDescendants().OfType<Border>()
            .First(b => b.Classes.Contains("preview-plate"));
        Check(previewPlate.Bounds.Height >= 116, "预览板有最小高度");
        // 短句一行 + 48px 还没到最小高度;换成长句(会换行)才撑得开板子。
        var originalBefore = model.Original;
        model.Original = "这一句歌词很长很长用来验证预览板会不会跟着字号长高并换行显示";
        Dispatcher.UIThread.RunJobs();
        Check(previewPlate.Bounds.Height > 116, "字号变大时预览板跟着长高");
        model.Original = originalBefore;
        fontSlider.Value = 32;
        Dispatcher.UIThread.RunJobs();
        Check(Math.Abs(previewText.FontSize - 32) < 0.01, "缩小字号时预览同步回落");

        // 新增外观:字体家族 / 颜色 / 描边 / 阴影即时生效 + 落盘
        var effectsTab = dialog.GetControl<Button>("TabEffects");
        effectsTab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Check(dialog.GetVisualDescendants().OfType<Slider>().Any(s => s.Bounds.Width > 0
            && Avalonia.Automation.AutomationProperties.GetName(s) == "描边粗细") == false, "描边关闭时不展开粗细滑块");
        model.OutlineEnabled = true;
        model.OutlineThickness = 2.5;
        model.OutlineColor = Avalonia.Media.Color.Parse("#FF102030");
        model.TextColor = Avalonia.Media.Color.Parse("#FFEEDDCC");
        model.ShadowEnabled = false;
        model.ShadowBlur = 6;
        model.FontFamilyName = "Consolas";
        Dispatcher.UIThread.RunJobs();
        Check(model.ShowOutline && model.EffectiveOutlineThickness == 2.5, "描边开启后提供有效宽度");
        Check(model.LyricShadow is null, "阴影关掉后不挂 Effect");
        Check((model.TextBrush as Avalonia.Media.SolidColorBrush)?.Color == Avalonia.Media.Color.Parse("#FFEEDDCC"),
            "文字颜色即时同步");
        Check(dialog.GetVisualDescendants().OfType<Slider>().Any(s => s.Bounds.Width > 0
            && Avalonia.Automation.AutomationProperties.GetName(s) == "描边粗细"), "描边开启后展开粗细滑块");
        state.Flush();
        var styled = new AppStateStore(Path.Combine(dir, "state.json"));
        Check(styled.DesktopLyricTextColor == "#FFEEDDCC" && styled.DesktopLyricOutlineEnabled
            && styled.DesktopLyricOutlineThickness == 2.5 && styled.DesktopLyricOutlineColor == "#FF102030"
            && !styled.DesktopLyricShadowEnabled && styled.DesktopLyricShadowBlur == 6
            && styled.DesktopLyricFontFamily == "Consolas",
            "外观设置落盘");

        // 恢复默认:只回外观,行为开关不动。
        model.ShowTranslation = false;
        model.ResetAppearanceCommand.Execute(null);
        Check(model.FontSize == 28 && model.OutlineThickness == 2 && !model.OutlineEnabled
            && model.FontFamilyName is null && model.ShadowEnabled && model.BackgroundOpacity == 0.85,
            "恢复默认外观");
        Check(!model.ShowTranslation, "恢复默认不动行为开关");

        model.ClickThrough = true;
        Check(main.TryHandleBack() && !main.IsDesktopLyricDialogOpen && !main.IsAnyModalOpen, "返回键关闭模态框");
        main.Settings.OpenDesktopLyricDialogCommand.Execute(null);
        Check(main.IsDesktopLyricDialogOpen, "穿透时仍可重新打开设置");
        // 截图前把布局跑完,否则 IsVisible 的变化还没反映到这一帧上(卡片留着展开时的排版)。
        Dispatcher.UIThread.RunJobs();
        using (var preview = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(360, 620)))
        {
            preview.Render(dialog);
            preview.Save(Path.Combine(Path.GetTempPath(), "aly-lyric-dialog-preview.png"), PngBitmapEncoderOptions.Default);
        }

        // 三个分段各截一张(桌面尺寸、暗色):检查分段标签/卡片/页脚的实际排版。
        var app = Avalonia.Application.Current!;
        app.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        dialogWindow.Width = 900;
        dialogWindow.Height = 760;
        main.Settings.DesktopLyrics!.OutlineEnabled = true;
        main.Settings.DesktopLyrics.TextColor = Avalonia.Media.Color.Parse("#FF72E5D3");
        main.Settings.DesktopLyrics.ShadowEnabled = true;
        var tabs = new[] { "TabGeneral", "TabText", "TabEffects" };
        for (var i = 0; i < tabs.Length; i++)
        {
            dialog.GetControl<Button>(tabs[i]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            using var shot = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(900, 760));
            shot.Render(dialog);
            shot.Save(Path.Combine(Path.GetTempPath(), $"aly-lyric-dialog-tab{i}.png"),
                PngBitmapEncoderOptions.Default);
        }
        // 48 号 + 长句再截一张:确认预览 1:1 放大后板子跟得上、不把字裁掉。
        main.Settings.DesktopLyrics!.FontSize = 48;
        main.Settings.DesktopLyrics.Original = "这一句歌词很长很长用来验证预览板会不会跟着字号长高并换行显示";
        dialog.GetControl<Button>("TabText").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        using (var big = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(900, 760)))
        {
            big.Render(dialog);
            big.Save(Path.Combine(Path.GetTempPath(), "aly-lyric-preview-font48.png"),
                PngBitmapEncoderOptions.Default);
        }
        dialog.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Escape });
        Check(!main.IsDesktopLyricDialogOpen, "Esc 关闭模态框");
        dialogWindow.Close();

        // 描边观感自查:两档粗细各导一张,8 层副本拼出来的轮廓要看得出是否毛糙。
        model.Enabled = true;
        model.OutlineColor = Avalonia.Media.Color.Parse("#FF000000");
        var styleWindow = new DesktopLyricWindow { DataContext = model };
        styleWindow.Show();
        Dispatcher.UIThread.RunJobs();
        foreach (var thickness in new[] { 1.5, 3.0 })
        {
            model.OutlineThickness = thickness;
            Dispatcher.UIThread.RunJobs();
            var styledContent = (Control)styleWindow.Content!;
            using var shot = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(
                (int)Math.Ceiling(styledContent.Bounds.Width), (int)Math.Ceiling(styledContent.Bounds.Height)));
            shot.Render(styledContent);
            shot.Save(Path.Combine(Path.GetTempPath(), $"aly-lyric-outline-{thickness:0.0}.png"),
                PngBitmapEncoderOptions.Default);
        }
        styleWindow.Close();
        Console.WriteLine("PASS: desktop lyrics");
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Dispatcher.UIThread.RunJobs();
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            Thread.Sleep(10);
        }
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: " + message);
    }
}
