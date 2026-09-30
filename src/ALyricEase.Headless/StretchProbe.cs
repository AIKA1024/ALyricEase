using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.ViewModels;
using ALyricEase.Views;

namespace ALyricEase.Headless;

/// <summary>行拉伸排查探针(--stretch):渲染搜索结果歌单行与"添加到歌单"弹窗歌单行的
/// 实际布局 PNG,核对行是否横向拉伸/文本省略是否生效。不触网、无副作用。</summary>
public static class StretchProbe
{
    public static void Run()
    {
        var main = ServiceLocator.Get<MainViewModel>();

        // 1) 搜索结果歌单行(全部页"歌单"分区):先播种数据再切页,避免触发网络加载
        var search = main.Search;
        for (var i = 1; i <= 4; i++)
        {
            search.Playlists.Add(new SearchPlaylistItemViewModel(new SearchPlaylistItem
            {
                Id = 100 + i,
                Source = MusicSource.QQ,
                Name = i == 2
                    ? "这是一个特别特别特别长的歌单名称用来验证文本省略号在拉伸状态下是否正确出现"
                    : $"探针歌单 {i}",
                Creator = $"创建者昵称{i}",
                TrackCount = 20 + i,
            }));
        }
        search.SelectedTab = SearchKind.All;
        search.HasSearched = true;
        Render(new SearchView { DataContext = search }, "stretch_search.png", 860, 720);

        // 2) 添加到歌单弹窗(QQ 源歌单候选)
        main.Playlist.QqPlaylists.Add(new PlaylistItemViewModel(new Playlist
        {
            Id = 200, DirId = 1, Source = MusicSource.QQ, Name = "QQ歌单C", TrackCount = 5, CanAddTracks = true,
        }));
        main.Playlist.QqPlaylists.Add(new PlaylistItemViewModel(new Playlist
        {
            Id = 201, DirId = 2, Source = MusicSource.QQ,
            Name = "名字很长的歌单名称用来测试弹窗里的省略号与行宽拉伸是否正常",
            TrackCount = 12, CanAddTracks = true,
        }));
        main.AddSongToPlaylistDialog.Refresh(new Song { Id = 1, Source = MusicSource.QQ, Name = "探针歌曲" });
        Render(new AddSongToPlaylistDialogView { DataContext = main }, "stretch_adddlg.png", 720, 560);
    }

    private static void Render(Control content, string fileName, double width, double height)
    {
        var win = new Window { Width = width, Height = height, Content = content };
        win.Show();
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();

        // 布局诊断:打印所有行 Button 的实际宽度/对齐/父容器宽度(判断哪一层没拉伸)
        foreach (var b in WalkButtons(win).Where(b => !b.Classes.Contains("link")))
            Console.WriteLine($"[stretch:{fileName}] Button '{(b.Classes.Contains("playlist-choice") ? "playlist-choice" : b.Classes.Contains("result-row") ? "result-row" : b.Name ?? "?")}'" +
                              $" width={b.Bounds.Width:F1} hAlign={b.HorizontalAlignment} parent={b.Parent?.GetType().Name} parentWidth={(b.Parent as Control)?.Bounds.Width:F1}");

        var px = new PixelSize((int)win.ClientSize.Width, (int)win.ClientSize.Height);
        using var rtb = new RenderTargetBitmap(px, new Vector(96, 96));
        rtb.Render(win);
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tmpandroid", fileName);
        rtb.Save(Path.GetFullPath(path));
        Console.WriteLine($"[stretch] 截图: {Path.GetFullPath(path)}");
    }

    private static IEnumerable<Button> WalkButtons(Control root)
    {
        foreach (var child in root.GetLogicalChildren())
        {
            if (child is Button b) yield return b;
            if (child is Control c)
                foreach (var inner in WalkButtons(c)) yield return inner;
        }
    }
}
