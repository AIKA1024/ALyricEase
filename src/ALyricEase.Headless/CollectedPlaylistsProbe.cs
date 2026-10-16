using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.Services.Auth;
using ALyricEase.ViewModels;
using ALyricEase.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;

namespace ALyricEase.Headless;

/// <summary>隔离账号数据，验证跨平台收藏身份、动态投影、键盘筛选与主题/窄屏布局。</summary>
internal static class CollectedPlaylistsProbe
{
    public static int Run()
    {
        Window? window = null;
        try
        {
            var root = Path.Combine(Path.GetTempPath(), "aly-collected-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            HeadlessApp.ConfigureServices(services =>
            {
                services.AddSingleton(new CookieStore(Path.Combine(root, "cookie.json")));
                services.AddSingleton(new AppStateStore(Path.Combine(root, "state.json")));
                services.AddSingleton(new MusicCacheService(64, Path.Combine(root, "cache"), new HttpClient()));
            });
            var library = ServiceLocator.Get<PlaylistViewModel>();
            var vm = ServiceLocator.Get<CollectedPlaylistsViewModel>();
            Check(vm.IsEmpty, "初始空状态");
            var netEase = Item(42, "晚风与海 · 值得循环的旋律", MusicSource.NetEase);
            var qq = Item(42, "城市夜行 / 爵士与霓虹", MusicSource.QQ);
            library.NetEaseCollectedPlaylists.Add(netEase);
            library.QqPlaylists.Add(qq);
            library.QqPlaylists.Add(Item(43, "自建歌单", MusicSource.QQ, dirId: 3));
            library.QqPlaylists.Add(Item(44, "我喜欢", MusicSource.QQ, dirId: 201));
            library.QqPlaylists.Add(Item(45, "可写歌单", MusicSource.QQ, writable: true));
            Check(vm.Playlists.SequenceEqual(new[] { netEase, qq }), "跨平台相同 ID 均保留，排除自建和喜欢");
            vm.SourceIndex = 2;
            Check(vm.Playlists.Count == 1 && ReferenceEquals(vm.Playlists[0], qq), "QQ 筛选保留来源及实例");
            library.QqPlaylists.Remove(qq);
            Check(vm.IsEmpty && vm.CountSummary == "0 个收藏歌单", "移除后同步空态和数量");
            library.QqPlaylists.Add(qq);
            qq.UpdateFrom(Item(42, qq.Name, MusicSource.QQ, dirId: 8));
            library.LibraryOverviewVersion++;
            Check(vm.IsEmpty, "原地更新归属后重新分类");
            qq.UpdateFrom(Item(42, qq.Name, MusicSource.QQ));
            library.LibraryOverviewVersion++;
            vm.SourceIndex = 0;
            var view = new CollectedPlaylistsView { DataContext = vm };
            window = new Window { Width = 900, Height = 640, Content = view };
            window.Bind(Window.BackgroundProperty, new DynamicResourceExtension("AppBackground"));
            Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
            window.Show();
            Drain();
            Check(view.FindControl<ItemsControl>("Rows")!.ItemCount == 2, "双平台行绑定");
            Check(window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "QQ 音乐"), "平台来源标签");
            Capture(window, root, "light");
            Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
            Drain();
            Capture(window, root, "dark");

            var filter = view.FindControl<ListBox>("PlatformFilter")!;
            ((ListBoxItem)filter.Items[0]!).Focus();
            window.KeyPress(Key.Right, Avalonia.Input.RawInputModifiers.None, PhysicalKey.ArrowRight, null);
            Drain();
            Check(vm.SourceIndex == 1 && vm.Playlists.Single() == netEase, "方向键筛选网易云");
            window.KeyPress(Key.Right, Avalonia.Input.RawInputModifiers.None, PhysicalKey.ArrowRight, null);
            Drain();
            Check(vm.SourceIndex == 2 && vm.Playlists.Single() == qq, "方向键筛选 QQ");

            vm.SourceIndex = 0;
            view.Classes.Add("narrow");
            window.Width = 390;
            Drain();
            var scroller = view.GetVisualDescendants().OfType<ScrollViewer>().First();
            Check(scroller.Extent.Width <= scroller.Viewport.Width + 1, "390px 无横向溢出");
            Check(window.GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("row-action")
                && b.IsEffectivelyVisible) == 2, "触屏每行始终可见更多菜单");
            Capture(window, root, "narrow-dark");
            Application.Current.RequestedThemeVariant = ThemeVariant.Light;
            Drain();
            Capture(window, root, "narrow-light");
            library.QqPlaylists.Clear();
            vm.SourceIndex = 2;
            Drain();
            Check(view.FindControl<Border>("EmptyState")!.IsVisible, "退出 QQ 后即时显示当前筛选空态");
            Capture(window, root, "empty");
            Console.WriteLine($"[collected-playlists] PASS; screenshots: {root}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[collected-playlists] FAIL: {ex}");
            return 1;
        }
        finally { window?.Close(); }
    }

    private static PlaylistItemViewModel Item(long id, string name, MusicSource source, long dirId = 0, bool writable = false)
        => new(new Playlist { Id = id, Name = name, Source = source, DirId = dirId, CanAddTracks = writable,
            TrackCount = 36, CoverUrl = "avares://ALyricEase/Assets/Placeholders/AlbumCoverPlaceholder.png" });

    private static void Drain()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Capture(Window window, string root, string name)
    {
        using var image = new RenderTargetBitmap(new PixelSize((int)window.ClientSize.Width,
            (int)window.ClientSize.Height), new Vector(96, 96));
        image.Render(window);
        image.Save(Path.Combine(root, name + ".png"), PngBitmapEncoderOptions.Default);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
