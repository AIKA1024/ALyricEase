using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.ViewModels;
using ALyricEase.Views;
using CommunityToolkit.Mvvm.Input;

namespace ALyricEase.Headless;

/// <summary>专辑详情页响应式几何回归：验证窄屏纵向头部和操作按钮不会越出卡片。</summary>
internal static class AlbumPageProbe
{
    public static int Run()
    {
        var vm = ServiceLocator.Get<AlbumViewModel>();
        vm.Name = "一张名字很长很长、需要在小屏完整展示更多内容的测试专辑";
        vm.ArtistName = "测试歌手与另一位测试歌手";
        vm.Artists = [new ArtistNavItem(1, "测试歌手")];
        vm.PublishTimeMs = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        // 「更多」按钮只在能拼出公开链接时出现;探针直接给链接,免得起真网络请求。
        vm.ShareLink = "https://music.163.com/album?id=1";
        vm.Songs.Add(new SongItemViewModel(
            new Song
            {
                Id = 1,
                Name = "测试歌曲",
                Artist = "测试歌手",
                ArtistIds = [1],
                ArtistNames = ["测试歌手"],
                Album = "曲目行不应重复显示的专辑名",
                AlbumId = 1,
                DurationMs = 180_000,
            },
            static _ => System.Threading.Tasks.Task.FromResult(true),
            index: 1));

        var failed = false;
        foreach (var qq in new[] { false, true })
        foreach (var (width, expectNarrow) in new[] { (400, true), (700, false), (1000, false) })
        {
            // 模拟真实加载顺序：视图先绑定空描述，再在请求完成后写入描述。
            vm.Description = "";
            var view = new AlbumView { DataContext = vm };
            var host = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions(width switch
                {
                    >= 900 => "320,*",
                    >= 641 => "48,*",
                    _ => "*",
                }),
            };
            Grid.SetColumn(view, width >= 641 ? 1 : 0);
            host.Children.Add(view);
            var window = new Window { Width = width, Height = 700, Content = host };
            window.Show();
            Drain();
            vm.Description = "这是一段用于检查专辑详情头部在窄屏下换行、占位与操作区边界的测试简介。";
            Drain();

            var card = view.FindControl<Border>("HeroCard")!;
            var horizontal = view.FindControl<Grid>("HeroHorizontal")!;
            var vertical = view.FindControl<StackPanel>("HeroVertical")!;
            var title = view.FindControl<TextBlock>("HeroVerticalTitle")!;
            var art = view.FindControl<Border>("HeroVerticalArt")!;
            var horizontalActions = view.FindControl<StackPanel>("HeroHorizontalActions")!;
            var verticalActions = view.FindControl<StackPanel>("HeroVerticalActions")!;
            var visibleActions = vertical.IsVisible ? verticalActions : horizontalActions;
            var artistButton = vertical.IsVisible
                ? view.FindControl<Button>("HeroVerticalArtistButton")!
                : view.FindControl<Button>("HeroHorizontalArtistButton")!;
            var description = vertical.IsVisible
                ? view.FindControl<TextBlock>("HeroVerticalDescription")!
                : view.FindControl<TextBlock>("HeroHorizontalDescription")!;

            var actualNarrow = vertical.IsVisible && !horizontal.IsVisible;
            var narrowSurfaceRemoved = !expectNarrow ||
                                       (card.BorderThickness == default &&
                                        card.Background is ISolidColorBrush { Color: { A: 0 } });
            var buttonsInside = visibleActions.GetVisualDescendants().OfType<Button>()
                .Where(button => button.IsVisible)
                .All(button => IsInside(button, card));
            var albumLinkRemoved = !view.GetVisualDescendants().OfType<TextBlock>()
                .Any(textBlock => textBlock.Text == "曲目行不应重复显示的专辑名");
            var narrowGeometryOk = !expectNarrow ||
                                   (Math.Abs(art.Bounds.Width - 240) < 0.1 &&
                                    title.Bounds.Width >= card.Bounds.Width - card.Padding.Left - card.Padding.Right - 3);
            var metadataOk = artistButton.IsEnabled && artistButton.Command is not null &&
                             description.IsVisible && description.Text == vm.Description;
            var artistPresenter = artistButton.GetVisualDescendants().OfType<ContentPresenter>()
                .First(presenter => presenter.Name == "PART_ContentPresenter");
            var artistText = artistButton.GetVisualDescendants().OfType<TextBlock>().First();
            var hyperlinkVisualOk = artistText.TextDecorations is { Count: > 0 } decorations &&
                                    Math.Abs(decorations[0].StrokeOffset - 2) < 0.01 &&
                                    decorations[0].StrokeOffsetUnit == TextDecorationUnit.Pixel &&
                                    artistText.FontWeight == FontWeight.SemiBold &&
                                    artistButton.Cursor?.ToString() == "Hand" &&
                                    artistText.Foreground is ISolidColorBrush artistForeground &&
                                    artistForeground.Color == Color.Parse("#64A8A1");
            var defaultArtistBackgroundTransparent = artistPresenter.Background is null ||
                                                     artistPresenter.Background is ISolidColorBrush { Color.A: 0 };
            var artistCenter = artistButton.TranslatePoint(
                new Point(artistButton.Bounds.Width / 2, artistButton.Bounds.Height / 2), window)!.Value;
            window.MouseMove(artistCenter);
            Drain();
            var hoverArtistBackgroundVisible = artistPresenter.Background is ISolidColorBrush { Color.A: > 0 };
            window.MouseMove(new Point(0, 0));
            Drain();
            var fluentArtistStyleOk = artistButton.Classes.Contains("artist-link") &&
                                      hyperlinkVisualOk && defaultArtistBackgroundTransparent &&
                                      hoverArtistBackgroundVisible;

            // 头部两个操作按钮要与歌单页同一套"方角"外观:play-all-square + hero-action-more,
            // 圆角 4、等高 36、间距 8;「更多」是字体字形而不是「•••」文字,菜单是 分享/复制链接。
            var playAll = visibleActions.GetVisualDescendants().OfType<Button>()
                .First(button => button.Classes.Contains("play-all"));
            var moreButton = vertical.IsVisible
                ? view.FindControl<Button>("HeroVerticalMore")!
                : view.FindControl<Button>("HeroHorizontalMore")!;
            var moreGlyph = moreButton.GetVisualDescendants().OfType<TextBlock>()
                .Select(textBlock => textBlock.Text ?? "")
                .FirstOrDefault(text => text.Length == 1 && text[0] >= '\uE000') ?? "";
            var menuHeaders = moreButton.Flyout is MenuFlyout moreFlyout
                ? moreFlyout.Items.OfType<MenuItem>().Select(item => item.Header as string).ToArray()
                : [];
            var squareActionOk = playAll.Classes.Contains("play-all-square") &&
                                 moreButton.Classes.Contains("hero-action-more") &&
                                 Math.Abs(playAll.CornerRadius.TopLeft - 4) < 0.01 &&
                                 Math.Abs(moreButton.CornerRadius.TopLeft - 4) < 0.01 &&
                                 Math.Abs(playAll.Bounds.Height - 36) < 0.1 &&
                                 Math.Abs(moreButton.Bounds.Height - 36) < 0.1 &&
                                 Math.Abs(visibleActions.Spacing - 8) < 0.01 &&
                                 Equals(playAll.Content, "▶ 播放全部");
            var moreMenuOk = moreButton.IsVisible && moreGlyph.Length == 1 &&
                             menuHeaders.Length == 2 && menuHeaders[0] == "分享" && menuHeaders[1] == "复制链接" &&
                             moreButton.Flyout is MenuFlyout { Items: var items } &&
                             items.OfType<MenuItem>().All(item => item.Icon is not null);
            // 菜单能真的弹出来(和歌单页一样挂在这个按钮上),否则只是"有数据结构"。
            if (moreButton.Flyout is MenuFlyout albumFlyout)
            {
                albumFlyout.ShowAt(moreButton);
                Drain();
                moreMenuOk &= albumFlyout.IsOpen;
                albumFlyout.Hide();
                Drain();
            }

            Console.WriteLine(
                $"[albumpage:{width}] classes={string.Join(',', view.Classes)} card={card.Bounds.Width:F0}x{card.Bounds.Height:F0} " +
                $"horizontal={horizontal.IsVisible} vertical={vertical.IsVisible} art={art.Bounds.Width:F0} " +
                $"titleWidth={title.Bounds.Width:F0} surfaceRemoved={narrowSurfaceRemoved} " +
                $"buttonsInside={buttonsInside} albumLinkRemoved={albumLinkRemoved} metadataOk={metadataOk} " +
                $"fluentArtistStyleOk={fluentArtistStyleOk} squareActionOk={squareActionOk} moreMenuOk={moreMenuOk} " +
                $"playAll={playAll.Bounds.Width:F0}x{playAll.Bounds.Height:F0} " +
                $"more={moreButton.Bounds.Width:F0}x{moreButton.Bounds.Height:F0}");

            if (actualNarrow != expectNarrow || !narrowSurfaceRemoved ||
                !buttonsInside || !albumLinkRemoved || !narrowGeometryOk || !metadataOk || !fluentArtistStyleOk ||
                !squareActionOk || !moreMenuOk)
                failed = true;

            var path = Path.Combine(Path.GetTempPath(), $"aly-album-{width}.png");
            using (var bitmap = new RenderTargetBitmap(
                       new PixelSize((int)window.ClientSize.Width, (int)window.ClientSize.Height),
                       new Vector(96, 96)))
            {
                bitmap.Render(window);
                bitmap.Save(path, PngBitmapEncoderOptions.Default);
            }
            Console.WriteLine($"[albumpage:{width}] 截图: {path}");
            var artistSelectionOk = VerifyArtistSelection(window, artistButton, vm, qq);
            Console.WriteLine($"[albumpage:{width}] qq={qq} artistSelectionOk={artistSelectionOk}");
            if (!artistSelectionOk) failed = true;
            window.Close();
        }

        vm.ShareLink = "";
        return failed ? 1 : 0;
    }

    private static bool VerifyArtistSelection(Window window, Button button, AlbumViewModel vm, bool qq)
    {
        var command = button.Command;
        var directOpens = 0;
        button.Command = new RelayCommand(() => directOpens++);
        var flyout = (MenuFlyout)button.Resources["ArtistMenuFlyout"]!;
        var ok = true;
        vm.Artists = qq
            ? [new ArtistNavItem(0, "第一位", "first-mid"), new ArtistNavItem(0, "第二位", "second-mid"), new ArtistNavItem(0, "无目标")]
            : [new ArtistNavItem(1, "第一位"), new ArtistNavItem(2, "第二位"), new ArtistNavItem(0, "无目标")];
        Drain();
        var center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        Drain();
        window.UpdateLayout();
        var items = window.GetVisualDescendants().OfType<MenuItem>()
            .Where(item => item.DataContext is ArtistNavItem artist && vm.Artists.Contains(artist)).ToArray();
        ok &= flyout.IsOpen && directOpens == 0 && items.Length == 3 &&
              Equals(items[1].Header, "第二位") &&
              ReferenceEquals(items[1].Command, vm.Artists[1].OpenCommand) &&
              items[1].IsEnabled && !items[2].IsEnabled;
        flyout.Hide();
        Drain();
        button.Focus();
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Drain();
        ok &= flyout.IsOpen && directOpens == 0;
        flyout.Hide();
        Drain();
        vm.Artists = [new ArtistNavItem(1, "测试歌手")];
        Drain();
        button.Focus();
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Drain();
        ok &= !flyout.IsOpen && directOpens == 1;
        vm.Artists = [];
        Drain();
        ok &= !button.IsEnabled;
        vm.Artists = [new ArtistNavItem(1, "测试歌手")];
        button.Command = command;
        return ok;
    }

    private static bool IsInside(Control child, Border card)
    {
        var origin = child.TranslatePoint(default, card);
        return origin is { } p &&
               p.X >= -0.1 && p.Y >= -0.1 &&
               p.X + child.Bounds.Width <= card.Bounds.Width + 0.1 &&
               p.Y + child.Bounds.Height <= card.Bounds.Height + 0.1;
    }

    private static void Drain()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
    }
}
