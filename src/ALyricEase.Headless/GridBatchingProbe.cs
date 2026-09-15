using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using ALyricEase.Controls;
using ALyricEase.Models;
using ALyricEase.ViewModels;

namespace ALyricEase.Headless;

/// <summary>验证逐项追加只在一个 Dispatcher 周期内触发一次全量网格分块。</summary>
public static class GridBatchingProbe
{
    public static int Run()
    {
        var songs = new ObservableCollection<SongItemViewModel>();
        var albums = new ObservableCollection<AlbumCardViewModel>();
        var songGrid = new SongGridView { ItemsSource = songs };
        var albumGrid = new AlbumGrid { ItemsSource = albums };
        var win = new Window
        {
            Width = 1000,
            Height = 800,
            Content = new StackPanel { Children = { songGrid, albumGrid } },
        };
        win.Show();
        Drain();

        var songBaseline = songGrid.RebuildCount;
        var albumBaseline = albumGrid.RebuildCount;
        foreach (var i in Enumerable.Range(1, 60))
        {
            songs.Add(new SongItemViewModel(
                new Song { Id = i, Name = $"歌曲{i}", DurationMs = 180_000 },
                (_, _, _) => Task.FromResult(true)));
            albums.Add(new AlbumCardViewModel(i, $"专辑{i}", ""));
        }

        var beforeDrain = songGrid.RebuildCount == songBaseline
            && albumGrid.RebuildCount == albumBaseline;
        Drain();
        var songDelta = songGrid.RebuildCount - songBaseline;
        var albumDelta = albumGrid.RebuildCount - albumBaseline;
        win.Close();
        Drain();
        var songDetachedBaseline = songGrid.RebuildCount;
        var albumDetachedBaseline = albumGrid.RebuildCount;
        songs.Add(new SongItemViewModel(
            new Song { Id = 61, Name = "离树歌曲", DurationMs = 180_000 },
            (_, _, _) => Task.FromResult(true)));
        albums.Add(new AlbumCardViewModel(61, "离树专辑", ""));
        Drain();
        var detachedIgnored = songGrid.RebuildCount == songDetachedBaseline
            && albumGrid.RebuildCount == albumDetachedBaseline;
        var passed = beforeDrain && songDelta == 1 && albumDelta == 1 && detachedIgnored;

        Console.WriteLine($"[grid-batching] 逐项追加 60 项: SongGrid 重建={songDelta}, AlbumGrid 重建={albumDelta}, " +
                          $"排空前未重建={beforeDrain}, 离树后未重建={detachedIgnored}");
        Console.WriteLine(passed ? "[grid-batching] PASS" : "[grid-batching] FAIL");
        return passed ? 0 : 1;
    }

    private static void Drain()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
    }
}
