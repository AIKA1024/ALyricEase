using System.Collections.Specialized;
using System.Diagnostics;
using System.Reflection;
using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.Services.Auth;
using ALyricEase.ViewModels;
using ALyricEase.Views;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;

namespace ALyricEase.Headless;

internal static class PlaybackListRefreshProbe
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "aly-list-refresh-" + Guid.NewGuid().ToString("N"));
        var state = new AppStateStore(Path.Combine(root, "state.json"));
        HeadlessApp.ConfigureServices(services =>
        {
            services.AddSingleton(state);
            services.AddSingleton(new CookieStore(Path.Combine(root, "cookie.json")));
            services.AddSingleton(new MusicCacheService(64, Path.Combine(root, "cache"), new HttpClient()));
        });
        var player = ServiceLocator.Get<PlayerViewModel>();
        var songs = Enumerable.Range(1, 100).Select(i => Song(i)).ToArray();
        foreach (var song in songs) state.RecordRecentSong(song);
        var recent = ServiceLocator.Get<RecentPlaybackViewModel>();
        var window = new Window { Width = 1100, Height = 700, Content = new RecentPlaybackView { DataContext = recent } };
        window.Show();
        async Task Drain() => await Dispatcher.UIThread.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.Background);
        await Drain();
        await Task.Delay(200);
        var oldRows = recent.Songs.ToArray();
        var oldImages = window.GetVisualDescendants().OfType<Image>().ToHashSet();
        var changes = 0;
        var resets = 0;
        recent.Songs.CollectionChanged += (_, e) => { changes++; if (e.Action == NotifyCollectionChangedAction.Reset) resets++; };
        var watch = Stopwatch.StartNew();
        state.RecordRecentSong(songs[^1]); // 同一首、相同顺序和元数据
        await Drain();
        var retainedRows = recent.Songs.Count(oldRows.Contains);
        var retainedImages = window.GetVisualDescendants().OfType<Image>().Count(oldImages.Contains);
        Console.WriteLine($"[playback-list-refresh] recent repeat: {watch.Elapsed.TotalMilliseconds:F1}ms changes={changes} resets={resets} rows={retainedRows}/100 images={retainedImages}/{oldImages.Count}");
        var recentOk = changes == 0 && retainedRows == 100 && retainedImages == oldImages.Count;

        player.SetQueue(songs, songs[0]);
        var queueChanges = 0;
        var queueResets = 0;
        player.UpcomingItems.CollectionChanged += (_, e) => { queueChanges++; if (e.Action == NotifyCollectionChangedAction.Reset) queueResets++; };
        void Refresh() => typeof(PlayerViewModel).GetMethod("RefreshUpcomingItems", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(player, null);
        void SetIndex(int index) => typeof(PlayerViewModel).GetField("_queueIndex", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(player, index);
        Refresh();
        Console.WriteLine($"[playback-list-refresh] queue unchanged: changes={queueChanges} resets={queueResets}");
        var queueOk = queueChanges == 0;
        queueChanges = 0;
        SetIndex(1);
        Refresh();
        Console.WriteLine($"[playback-list-refresh] queue next: changes={queueChanges} resets={queueResets}");
        queueOk &= queueChanges == 2 && queueResets == 0
            && player.UpcomingItems.Select(p => p.Song.Id).SequenceEqual(songs.Skip(2).Append(songs[0]).Select(p => p.Id));
        try
        {
            Check(recentOk && queueOk, "Unchanged rows or queue were rebuilt.");
            state.RecordRecentSong(songs[0]);
            await Drain();
            Check(recent.Songs[0].Song.Id == 1 && recent.Songs.All(oldRows.Contains), "History reorder lost rows.");
            Check(recent.Songs.Select(p => p.Index).SequenceEqual(Enumerable.Range(1, 100)), "History indices are stale.");
            var queueField = typeof(SongItemViewModel).GetField("_queue", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Check(recent.Songs.All(p => ((IReadOnlyList<Song>)queueField.GetValue(p)!).Select(s => s.Id).SequenceEqual(state.RecentSongs.Select(s => s.Id))), "History playback bindings are stale.");
            state.RecordRecentSong(Song(1, "更新后的歌名"));
            Check(recent.Songs[0].Name == "更新后的歌名" && recent.Songs.Skip(1).All(oldRows.Contains), "Metadata change replaced unrelated rows.");
            state.RecordRecentSong(Song(101));
            Check(recent.Songs.Count == 100 && recent.Songs.Any(p => p.Song.Id == 101), "History cap/insert failed.");
            player.UpcomingItems[0].RemoveCommand.Execute(null);
            Check(player.UpcomingItems.All(p => p.Song.Id != 3) && queueResets == 0, "Remove command or queue reuse failed.");
            player.CurrentSong = songs[1];
            player.PlaySongNext(songs[20]);
            Check(player.UpcomingItems[0].Song.Id == 21 && player.UpcomingItems.Count(p => p.Song.Id == 21) == 1, "Play next order/dedup failed.");

            // 稀疏物化窗口：逻辑序号不能用窗口长度折算，-1 是“下一首播放”插入项。
            player.SetQueue(songs.Take(6).ToArray(), songs[2]);
            typeof(PlayerViewModel).GetField("_lazyQueue", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(player,
                new IndexedSongQueue(Enumerable.Range(1, 500).Select(i => (long)i).ToArray(), [], (_, _) => Task.FromResult(new List<Song>())));
            var indices = (List<int>)typeof(PlayerViewModel).GetField("_lazyQueueVmIndices", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(player)!;
            indices.AddRange([0, 99, 100, 300, -1, -1]);
            SetIndex(100);
            Refresh();
            Check(player.UpcomingItems.Select(p => p.Song.Id).SequenceEqual([5L, 6, 4, 1, 2]), "Sparse lazy queue/sentinel order failed.");
            player.UpcomingItems.Single(p => p.Song.Id == 2).RemoveCommand.Execute(null);
            Check(player.UpcomingItems.Select(p => p.Song.Id).SequenceEqual([5L, 6, 4, 1]), "Lazy queue exclusion failed.");
            queueChanges = 0;
            Refresh();
            Check(queueChanges == 0 && queueResets == 0, "Unchanged lazy queue rebuilt rows.");
            state.ClearRecentSongs();
            Check(recent.ShowEmpty && recent.Songs.Count == 0 && resets == 0, "History clear failed.");
            SetIndex(-1);
            Refresh();
            Check(player.UpcomingItems.Count == 0 && queueResets == 0, "Empty queue failed.");
            var qqA = new Song { Source = MusicSource.QQ, Mid = "qq-a", Name = "QQ A" };
            var qqB = new Song { Source = MusicSource.QQ, Mid = "qq-b", Name = "QQ B" };
            state.RecordRecentSong(qqA);
            state.RecordRecentSong(qqB);
            var qqRows = recent.Songs.ToArray();
            state.RecordRecentSong(qqA);
            Check(recent.Songs.Count == 2 && recent.Songs.All(qqRows.Contains) && recent.Songs[0].Song.Mid == "qq-a", "QQ mid identity failed.");
            var local = new Song { Source = MusicSource.Local, Id = -1, Name = "本地歌曲", LocalFilePath = Path.Combine(root, "local.mp3") };
            state.RecordRecentSong(local);
            state.Flush();
            Check(new AppStateStore(Path.Combine(root, "state.json")).RecentSongs.Single(s => s.Source == MusicSource.Local).LocalFilePath == local.LocalFilePath,
                "Local history lost its playback path.");
            Console.WriteLine("[playback-list-refresh] PASS: reuse, metadata, bindings, cap, remove, play-next, lazy order, QQ identity, local path, clear");
        }
        finally { window.Close(); state.Flush(); }
    }

    private static Song Song(int id, string? name = null) => new()
    {
        Id = id, Source = (MusicSource)99, Name = name ?? $"歌曲 {id}", Artist = "测试歌手",
        CoverUrl = CoverImageConverter.PlaceholderUri
    };

    private static void Check(bool success, string message)
    {
        if (!success) throw new InvalidOperationException(message);
    }
}
