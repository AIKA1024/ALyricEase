using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.Services.Audio;
using ALyricEase.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ALyricEase.Headless;

/// <summary>FM 发现页：主题/布局、详情入口、加载反馈、空态、离页再进入。全程离线。</summary>
internal static class PersonalFmProbe
{
    public static int Run()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aly-personal-fm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var main = QqUserFailureProbe.CreateOfflineMain(root);
        var player = main.Player;
        var audio = (HeadlessApp.StubAudioPlayer)ServiceLocator.Get<IAudioPlayer>();
        audio.SetAudioAnalysisEnabled(true);
        var view = new PersonalFmView { DataContext = player };
        var window = new Window { Width = 920, Height = 640, Content = view, RequestedThemeVariant = ThemeVariant.Light };
        try
        {
            window.Show();
            Frames();
            Check(audio.AudioAnalysisEnabled, "静态背景不关闭其他视图的音频分析");
            Check(view.FindControl<StackPanel>("EmptyState")!.IsVisible, "未登录显示入口");
            Check(!view.FindControl<Grid>("ListeningBody")!.IsVisible, "未启动不展示其他来源的歌曲");
            view.FindControl<Button>("StartButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(main.IsLoginDialogOpen, "未登录按钮打开登录弹窗");
            main.CloseLoginDialogCommand.Execute(null);

            var path = Path.Combine(root, "song.mp3");
            var song = new Song { Id = 101, Name = "风经过的地方", Artist = "夏日来信", Album = "山野之间",
                Source = MusicSource.Local, LocalFilePath = path, DurationMs = 360_000 };
            player.CurrentSong = song;
            player.Title = song.Name;
            player.Artist = song.Artist;
            player.DurationMs = song.DurationMs;
            player.IsFmActive = true;
            player.Message = null;
            Frames();
            Check(!view.GetVisualDescendants().OfType<LyricView>().Any(), "发现页不再加载歌词视图");
            player.PositionMs = 120_000;
            var cover = view.FindControl<Button>("CoverButton")!;
            var point = cover.TranslatePoint(new Point(cover.Bounds.Width / 2, cover.Bounds.Height / 2), window)!.Value;
            window.MouseMove(point);
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Frames();
            Check(main.ShowNowPlaying && ReferenceEquals(player.CurrentSong, song) && player.PositionMs == 120_000,
                "点击封面进入详情，保留歌曲与进度");
            main.CloseNowPlayingCommand.Execute(null);
            cover.Focus();
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Check(main.ShowNowPlaying, "封面入口支持键盘 Enter");
            main.CloseNowPlayingCommand.Execute(null);
            window.MouseMove(new Point(4, 4));
            player.IsCurrentLiked = true;
            Check(view.FindControl<Button>("LikeButton")!.Classes.Contains("liked"), "喜欢状态同步");
            player.IsLoading = true;
            Check(!view.FindControl<Button>("NextButton")!.IsEnabled, "加载中禁止重复换歌");
            Frames(20);
            Check(view.Classes.Contains("loading") && view.FindControl<Border>("Artwork")!.Opacity < 0.8,
                "加载时封面淡出并显示寻找提示");
            player.IsLoading = false;
            Frames(20);
            Check(!view.Classes.Contains("loading") && view.FindControl<Border>("Artwork")!.Opacity > 0.99,
                "加载结束恢复封面");

            foreach (var (width, height) in new[] { (920, 640), (640, 520), (620, 600), (360, 560) })
            {
                window.Width = width; window.Height = height;
                foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                {
                    window.RequestedThemeVariant = theme;
                    Frames();
                    var artwork = view.FindControl<Border>("Artwork")!;
                    var title = view.FindControl<TextBlock>("SongTitle")!;
                    Check(Math.Abs(artwork.Bounds.Width - artwork.Bounds.Height) < 1, "封面保持正方形");
                    var info = view.FindControl<StackPanel>("SongPanel")!;
                    var infoPoint = info.TranslatePoint(default, view)!.Value;
                    var artPoint = artwork.TranslatePoint(default, view)!.Value;
                    Check(width < 640 ? infoPoint.Y > artPoint.Y + artwork.Bounds.Height : infoPoint.X > artPoint.X + artwork.Bounds.Width,
                        $"{width} 响应式分栏");
                    Check(title.Bounds.Width <= width && info.Bounds.Width > 100, "歌曲信息具有有限宽度");
                    Check(view.FindControl<ScrollViewer>("PageScroll")!.Extent.Width <= view.Bounds.Width + 1, "无横向溢出");
                    Check(width >= 640 || !view.FindControl<Border>("ListeningBadgeHost")!.IsVisible, "窄屏收起提示");
                    var actions = view.FindControl<StackPanel>("Actions")!;
                    var actionsPoint = actions.TranslatePoint(default, view)!.Value;
                    Check(actionsPoint.Y + actions.Bounds.Height <= view.Bounds.Height, "常用操作在首屏可见");
                    Capture(view, root, $"fm-{width}-{theme}");
                }
            }

            window.Width = 920; window.Height = 640;
            window.Content = null;
            player.IsLoading = true;
            Check(!view.Classes.Contains("loading"), "离页解除播放器状态订阅");
            window.Content = view;
            Check(view.Classes.Contains("loading"), "重新进入恢复当前状态");
            player.IsLoading = false;
            player.Title = "第二首，一段很长很长的歌名，需要正确换行并保持操作按钮始终可用";
            player.Artist = "新的歌手";
            Frames();
            Check(view.FindControl<TextBlock>("SongTitle")!.Text == player.Title, "换歌更新曲目信息");
            Capture(view, root, "fm-long-title");
            player.IsFmActive = false;
            player.Message = "私人FM获取失败,请稍后重试";
            Frames();
            Check(view.FindControl<StackPanel>("EmptyState")!.IsVisible && !view.FindControl<Grid>("ListeningBody")!.IsVisible,
                "FM 停止时恢复重试入口");
            Capture(view, root, "fm-empty");
            Console.WriteLine($"[personal-fm] PASS; screenshots: {root}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[personal-fm] FAIL {ex}");
            return 1;
        }
        finally { window.Close(); }
    }

    private static void Frames(int count = 8)
    {
        for (var i = 0; i < count; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(16);
        }
    }

    private static void Capture(Control view, string root, string name)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)view.Bounds.Width, (int)view.Bounds.Height), new Vector(96, 96));
        bitmap.Render(view);
        bitmap.Save(Path.Combine(root, name + ".png"), PngBitmapEncoderOptions.Default);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine($"[personal-fm] OK {message}");
    }
}
