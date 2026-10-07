using ALyricEase.Controls;
using ALyricEase.Infrastructure;
using ALyricEase.Services;
using ALyricEase.Services.NetEase;
using ALyricEase.Services.QQMusic;
using ALyricEase.ViewModels;
using ALyricEase.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ALyricEase.Headless;

/// <summary>全部专辑显示模式的隔离持久化、响应式布局和大集合虚拟化回归，不触网。</summary>
internal static class AlbumDisplayModeProbe
{
    public static int Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"aly-album-display-{Guid.NewGuid():N}");
        var statePath = Path.Combine(directory, "state.json");
        try
        {
            var state = new AppStateStore(statePath);
            var vm = CreateViewModel(state);
            Assert(!vm.IsListMode && state.ArtistAlbumsListMode is null, "桌面首次默认大图，尚未保存用户选择");
            state.Save();
            Assert(new AppStateStore(statePath).ArtistAlbumsListMode is null, "其他状态保存不覆盖平台默认模式");
            vm.IsListMode = true;
            Assert(CreateViewModel(new AppStateStore(statePath)).IsListMode, "重新加载恢复列表模式");
            vm.IsListMode = false;
            Assert(!CreateViewModel(new AppStateStore(statePath)).IsListMode &&
                   new AppStateStore(statePath).ArtistAlbumsListMode == false, "显式大图选择持久化");
            vm.IsListMode = true;
            vm.Name = "测试歌手";
            vm.Subtitle = "已加载 2000 张专辑";
            vm.Albums.AddRange(Enumerable.Range(1, 2000).Select(i => new AlbumCardViewModel(
                i, i == 1 ? "一张名字很长很长很长的测试专辑，用于检查标题截断" : $"专辑 {i}", "",
                publishTimeMs: new DateTimeOffset(2025, 12, 31, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                songCount: i == 1 ? 0 : 12)).ToList());

            foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            foreach (var width in new[] { 360, 400, 700, 1000 })
                VerifyView(vm, width, theme);

            vm.ReleaseCurrentPageData();
            Assert(vm.IsListMode && CreateViewModel(new AppStateStore(statePath)).IsListMode,
                "离开专辑页后仍保留列表偏好");
            Console.WriteLine("[album-display] PASS");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[album-display] FAIL: {ex}");
            return 1;
        }
        finally
        {
            if (File.Exists(statePath)) File.Delete(statePath);
            if (File.Exists(statePath + ".tmp")) File.Delete(statePath + ".tmp");
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }

    private static ArtistAlbumsPageViewModel CreateViewModel(AppStateStore state) => new(
        ServiceLocator.Get<NetEaseApiClient>(), ServiceLocator.Get<QQMusicApiClient>(),
        ServiceLocator.Get<MusicCacheService>(), state);

    private static void VerifyView(ArtistAlbumsPageViewModel vm, int width, ThemeVariant theme)
    {
        var view = new ArtistAlbumsPageView { DataContext = vm };
        var window = new Window { Width = width, Height = 640, Content = view, RequestedThemeVariant = theme };
        window.Show();
        try
        {
            Drain();
            var list = view.GetVisualDescendants().OfType<AlbumList>().Single();
            var grid = view.GetVisualDescendants().OfType<AlbumGrid>().Single();
            Assert(list.IsVisible && !grid.IsVisible, $"{width}/{theme}: 列表模式显示正确");
            var rows = ListRows(list);
            Assert(rows.Length is > 0 and < 60, $"{width}/{theme}: 2000 张专辑仅实化 {rows.Length} 行");
            var row = rows.First(r => r.DataContext is AlbumCardViewModel { Id: 1 });
            var album = (AlbumCardViewModel)row.DataContext!;
            Assert(ReferenceEquals(row.Command, album.OpenCommand), "列表行使用原有专辑导航命令");
            var title = row.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("album-title"));
            var metadata = row.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Classes.Contains("album-metadata"));
            Assert(metadata.Orientation == (width < 641 ? Orientation.Vertical : Orientation.Horizontal),
                $"{width}/{theme}: 发布日期与歌曲数排列正确");
            Assert(title.Bounds.Width >= 80 && IsInside(title, row) && IsInside(metadata, row),
                $"{width}/{theme}: 长标题和右侧信息没有溢出");
            var count = metadata.Children.OfType<TextBlock>().Single(t => t.Classes.Contains("album-song-count"));
            album.UpdateSongCount(18);
            Drain();
            Assert(count.Text == "18 首歌", "异步回填曲数后已显示的行即时更新");

            var mode = view.FindControl<ToggleButton>("DisplayModeButton")!;
            var filter = view.GetVisualDescendants().OfType<ToggleButton>()
                .Single(b => b != mode && b.Classes.Contains("filter-expander"));
            Assert(IsInside(mode, view) && IsInside(filter, view) &&
                   mode.TranslatePoint(default, view)!.Value.X + mode.Bounds.Width <=
                   filter.TranslatePoint(default, view)!.Value.X, "模式按钮位于筛选按钮左侧且没有溢出");

            using (var bitmap = new RenderTargetBitmap(new PixelSize(width, 640), new Vector(96, 96)))
            {
                bitmap.Render(window);
                var path = Path.Combine(Path.GetTempPath(), $"aly-album-list-{width}-{theme}.png");
                bitmap.Save(path, PngBitmapEncoderOptions.Default);
                Console.WriteLine($"[album-display] 截图: {path}");
            }

            Click(window, mode);
            Assert(!vm.IsListMode && !list.IsVisible && grid.IsVisible, "点击按钮切换大图");
            Click(window, mode);
            Assert(vm.IsListMode && list.IsVisible && !grid.IsVisible, "再次点击恢复列表");

            var scroller = view.FindControl<ScrollViewer>("PageScroller")!;
            scroller.ScrollToEnd();
            Drain();
            var endRows = ListRows(list);
            Assert(endRows.Length < 60 && endRows.Any(r => r.DataContext is AlbumCardViewModel { Id: 2000 }),
                "滚动到底部可显示最后一张专辑，实化数量保持有界");
        }
        finally { window.Close(); }
    }

    private static Button[] ListRows(AlbumList list) => list.GetVisualDescendants().OfType<Button>()
        .Where(b => b.Classes.Contains("album-list-row")).ToArray();

    private static void Click(Window window, Control control)
    {
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Drain();
    }

    private static bool IsInside(Control child, Control parent)
    {
        var origin = child.TranslatePoint(default, parent)!.Value;
        return origin.X >= -0.1 && origin.Y >= -0.1 &&
               origin.X + child.Bounds.Width <= parent.Bounds.Width + 0.1 &&
               origin.Y + child.Bounds.Height <= parent.Bounds.Height + 0.1;
    }

    private static void Drain()
    {
        for (var i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs();
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine($"[album-display] PASS {message}");
    }
}
