using System;
using System.Collections.Generic;
using System.Linq;
using ALyricEase.Models;
using ALyricEase.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Threading;

namespace ALyricEase.Headless;

/// <summary>菜单高度探针(--menuheight):实测各处 MenuFlyoutPresenter 的高度,
/// 用来核对 FlyoutOpenAnimation 的入场偏移——固定 50px 或 50% 高度都会在首帧露出过多内容;
/// 新规则使用完整弹层高度,让各种高度的菜单都从裁剪区外开始。</summary>
public static class MenuHeightProbe
{
    private sealed class CapturingMenuFlyout : MenuFlyout
    {
        public MenuFlyoutPresenter? Presenter { get; private set; }

        protected override Control CreatePresenter()
        {
            Presenter = (MenuFlyoutPresenter)base.CreatePresenter();
            return Presenter;
        }
    }

    public static void Run()
    {
        var button = new Button { Content = "锚点" };
        var win = new Window
        {
            Width = 900,
            Height = 700,
            Content = new StackPanel { Margin = new Thickness(40), Children = { button } },
        };
        win.Show();
        Dispatcher.UIThread.RunJobs();

        var rows = new List<(string Name, double Height)>();

        Measure(rows, "每日推荐 歌手/专辑(2 项)", () =>
        {
            var menu = new CapturingMenuFlyout();
            menu.Items.Add(IconItem("表演者: CIEL"));
            menu.Items.Add(IconItem("专辑: 空想少女"));
            return menu;
        }, button);

        Measure(rows, "侧栏歌单右键(3 项 + 分隔线)", () =>
        {
            var menu = new CapturingMenuFlyout();
            menu.Items.Add(new MenuItem { Header = "重命名歌单" });
            menu.Items.Add(new MenuItem { Header = "复制链接" });
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = "删除歌单" });
            return menu;
        }, button);

        Measure(rows, "播放条歌曲菜单(10 项)", () =>
        {
            var song = new Song
            {
                Id = 123,
                Name = "空中散步",
                Artist = "CIEL / 测试歌手",
                ArtistIds = new long[] { 10, 11 },
                ArtistNames = new[] { "CIEL", "测试歌手" },
                Album = "空想少女",
                AlbumId = 20,
            };
            var flyout = new PlayerBarView().CreateSongMenu(song, "咸咸的鱼王喜欢的音乐");
            return new WrapperMenuFlyout(flyout);
        }, button);

        Console.WriteLine();
        Console.WriteLine($"[menuheight] {"菜单",-34}{"高度",10}{"50px 偏移裁掉",16}{"新偏移",10}{"新比例",10}");
        foreach (var (name, height) in rows)
        {
            var newOffset = ALyricEase.Controls.FlyoutOpenAnimation.ComputeEntranceOffset(height);
            var oldRatio = Math.Min(1, 50 / height);
            var newRatio = Math.Min(1, newOffset / height);
            Console.WriteLine($"[menuheight] {name,-34}{height,10:F0}{oldRatio,15:P0}{newOffset,10:F0}{newRatio,10:P0}");
        }

        win.Close();
    }

    /// <summary>把现建的 MenuFlyout 包一层,以便拿到 CreatePresenter 产出的表面。</summary>
    private sealed class WrapperMenuFlyout : MenuFlyout
    {
        private readonly MenuFlyout _inner;

        public WrapperMenuFlyout(MenuFlyout inner)
        {
            _inner = inner;
            foreach (var item in inner.Items.ToList())
            {
                inner.Items.Remove(item);
                Items.Add(item);
            }

            foreach (var c in inner.FlyoutPresenterClasses)
            {
                FlyoutPresenterClasses.Add(c);
            }
        }

        public MenuFlyoutPresenter? Presenter { get; private set; }

        protected override Control CreatePresenter()
        {
            Presenter = (MenuFlyoutPresenter)base.CreatePresenter();
            return Presenter;
        }
    }

    private static MenuItem IconItem(string header) => new()
    {
        Header = header,
        Icon = new TextBlock { Text = "\uE921", FontSize = 14, VerticalAlignment = VerticalAlignment.Center },
    };

    private static void Measure(List<(string, double)> rows, string label,
        Func<CapturingMenuFlyout> factory, Button anchor)
    {
        var menu = factory();
        menu.ShowAt(anchor);
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
        var height = menu.Presenter?.Bounds.Height ?? double.NaN;
        rows.Add((label, height));
        menu.Hide();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Measure(List<(string, double)> rows, string label,
        Func<WrapperMenuFlyout> factory, Button anchor)
    {
        var menu = factory();
        menu.ShowAt(anchor);
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
        var height = menu.Presenter?.Bounds.Height ?? double.NaN;
        rows.Add((label, height));
        menu.Hide();
        Dispatcher.UIThread.RunJobs();
    }
}
