using System.Collections.Specialized;
using System.Net.Http;
using System.Reflection;
using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.Services.NetEase;
using ALyricEase.Services.QQMusic;
using ALyricEase.ViewModels;
using ALyricEase.Views;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ALyricEase.Headless;

/// <summary>聚合歌单流式加载的无网络回归：稳定来源排序、批尺寸和单次范围通知。
/// 另覆盖:成员曲数预估、LocalTracks 的 state.json 往返、离线打开时本地歌曲行仍展示。</summary>
internal static class AggregateLoadingProbe
{
    public static async Task<int> RunAsync()
    {
        var aggregate = new AggregatePlaylist
        {
            SourceOrder = AggregateSourceOrder.QqFirst,
            Members =
            [
                Member(MusicSource.NetEase, 1),
                Member(MusicSource.QQ, 2),
                Member(MusicSource.NetEase, 3),
                Member(MusicSource.QQ, 4),
            ],
        };
        var order = PlaylistViewModel.OrderAggregateMembers(aggregate).Select(member => member.PlaylistId).ToArray();
        var stableOrder = order.SequenceEqual([2L, 4L, 1L, 3L]);

        var rows = new RangeObservableCollection<int>();
        var notifications = 0;
        var notifiedItems = 0;
        rows.CollectionChanged += (_, e) =>
        {
            notifications++;
            if (e.Action == NotifyCollectionChangedAction.Add)
                notifiedItems += e.NewItems?.Count ?? 0;
        };
        rows.AddRange(Enumerable.Range(0, 120).ToList());
        var ranged = rows.Count == 120 && notifications == 1 && notifiedItems == 120;

        var item = new PlaylistItemViewModel(new Playlist());
        var countChanged = 0;
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(PlaylistItemViewModel.TrackCount)
                or nameof(PlaylistItemViewModel.TrackCountText))
                countChanged++;
        };
        item.UpdateTrackCount(120);
        var liveCount = item.TrackCount == 120 && item.TrackCountText == "120 首" && countChanged == 2;

        var batchSizes = PlaylistViewModel.AggregateNetEaseBatchSize == 100
            && PlaylistViewModel.AggregateQqPageSize == 300;
        var virtualized = VerifyPlaylistVirtualization();
        // 聚合头部曲数预估:打开时按成员在资料库中的曲数加总(在线真实曲数全部拉完后接管)。
        var vm = ServiceLocator.Get<PlaylistViewModel>();
        vm.Playlists.Add(new PlaylistItemViewModel(new Playlist { Id = 1, TrackCount = 50 }));
        vm.Playlists.Add(new PlaylistItemViewModel(new Playlist { Id = 3, TrackCount = 28 }));
        vm.QqPlaylists.Add(new PlaylistItemViewModel(new Playlist { Id = 2, TrackCount = 30 }));
        vm.QqPlaylists.Add(new PlaylistItemViewModel(new Playlist { Id = 4, TrackCount = 12 }));
        var estimate = vm.EstimateAggregateTrackCount(aggregate.Members);
        // 查不到的成员(Playlists 里没有 id=5)按 0 计,不影响其余成员加总。
        var estimateSkipsMissing = vm.EstimateAggregateTrackCount(
            aggregate.Members.Append(Member(MusicSource.NetEase, 5)).ToList()) == 120;
        Console.WriteLine($"[aggregate-load] estimate={estimate}, estimateSkipsMissing={estimateSkipsMissing}");
        // 本地音乐歌单持久化往返:state.json 写→读后 Tracks 必须原样保留(否则重开看不到导入的歌)。
        var tempState = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"aly-agg-state-{Guid.NewGuid():N}.json");
        var stateStore = new AppStateStore(tempState);
        stateStore.LocalPlaylists.Add(new LocalPlaylist
        {
            Id = "lp1",
            Name = "持久化",
            Tracks = [@"C://music//a.mp3", @"C://music//b.flac"],
        });
        stateStore.AggregatePlaylists.Add(new AggregatePlaylist
        {
            Id = "agg1",
            Name = "持久化",
            Members =
            [
                new AggregatePlaylistMember { Source = MusicSource.NetEase, PlaylistId = 1, PlaylistName = "m1" },
                // 本地成员:PlaylistId 恒 0,靠 LocalPlaylistId 引用 —— Load 的有效性过滤必须保留它
                new AggregatePlaylistMember { Source = MusicSource.Local, LocalPlaylistId = "lp1", PlaylistName = "l" },
            ],
        });
        stateStore.Save();
        var reloadedState = new AppStateStore(tempState);
        var reloaded = reloadedState.LocalPlaylists[0].Tracks;
        var reloadedAgg = reloadedState.AggregatePlaylists[0];
        var roundTrip = reloaded is { Count: 2 } && reloaded[0] == @"C://music//a.mp3"
            && reloadedAgg.Members.Count == 2
            && reloadedAgg.Members[1].Source == MusicSource.Local
            && reloadedAgg.Members[1].LocalPlaylistId == "lp1";
        Console.WriteLine($"[aggregate-load] localTracksRoundTrip={roundTrip}");
        // 离线打开含"本地音乐歌单成员"的聚合歌单:网络成员全部失败,本地成员曲目仍应显示在末尾。
        // 本地歌单以内存注入方式提供给 VM(AppState 不落盘),避免污染真实 state.json。
        var netEaseApi = ServiceLocator.Get<NetEaseApiClient>();
        var qqApi = ServiceLocator.Get<QQMusicApiClient>();
        typeof(NetEaseApiClient).GetField("_http", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(netEaseApi, new HttpClient(new OfflineHandler()));
        typeof(QQMusicApiClient).GetField("_http", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(qqApi, new HttpClient(new OfflineHandler()));
        var playlistVm = ServiceLocator.Get<PlaylistViewModel>();
        var appState = ServiceLocator.Get<MainViewModel>().AppState;
        appState.LocalPlaylists.Add(new LocalPlaylist
        {
            Id = "probe-local",
            Name = "探针本地歌单",
            Tracks = new List<string> { @"C://music//local1.mp3", @"C://music//local2.mp3" },
        });
        var offlineAggregate = new AggregatePlaylist
        {
            Id = "agg-offline",
            Name = "含本地成员",
            Members =
            [
                new AggregatePlaylistMember { Source = MusicSource.NetEase, PlaylistId = 1, PlaylistName = "m1" },
                new AggregatePlaylistMember { Source = MusicSource.Local, LocalPlaylistId = "probe-local", PlaylistName = "探针本地歌单" },
            ],
        };
        await playlistVm.OpenAggregateCommand.ExecuteAsync(offlineAggregate);
        var localRowsShown = playlistVm.Tracks.Count == 2
            && playlistVm.Tracks.All(row => row.IsLocal)
            && playlistVm.Tracks.All(row => row.IsLocalFileMissing)
            // 文件缺失行必须保持整行可交互(IsRowEnabled),否则右键菜单/打开文件位置全失效
            && playlistVm.Tracks.All(row => row.IsRowEnabled);
        var renumbered = playlistVm.Tracks.Select(row => row.Index).SequenceEqual([1, 2]);
        Console.WriteLine($"[aggregate-load] tracks={playlistVm.Tracks.Count}, " +
            $"isLocalFlags=[{string.Join(',', playlistVm.Tracks.Select(t => t.IsLocal))}], " +
            $"missingFlags=[{string.Join(',', playlistVm.Tracks.Select(t => t.IsLocalFileMissing))}], " +
            $"indices=[{string.Join(',', playlistVm.Tracks.Select(t => t.Index))}], " +
            $"localRowsShown={localRowsShown}, renumbered={renumbered}");
        appState.LocalPlaylists.RemoveAt(appState.LocalPlaylists.Count - 1);

        // 无封面本地歌单:头部 CoverUrl 必须落到应用默认封面(avares 资源)
        await playlistVm.OpenLocalPlaylistCommand.ExecuteAsync(
            new LocalPlaylist { Id = "lp-nocover", Name = "无封面歌单" });
        var defaultCoverApplied = playlistVm.SelectedPlaylist?.Playlist.CoverUrl
            == PlaylistViewModel.DefaultCoverUri;
        Console.WriteLine($"[aggregate-load] defaultCoverApplied={defaultCoverApplied} " +
            $"(cover={playlistVm.SelectedPlaylist?.Playlist.CoverUrl})");
        return stableOrder && ranged && liveCount && batchSizes && virtualized
            && estimate == 120 && estimateSkipsMissing && roundTrip && localRowsShown && renumbered
            && defaultCoverApplied ? 0 : 1;
    }

    private static AggregatePlaylistMember Member(MusicSource source, long id) => new()
    {
        Source = source,
        PlaylistId = id,
        PlaylistName = $"{source}-{id}",
    };

    private static bool VerifyPlaylistVirtualization()
    {
        var vm = ServiceLocator.Get<PlaylistViewModel>();
        vm.IsLoggedIn = true;
        vm.IsLoadingMore = true;
        vm.SelectedPlaylist = new PlaylistItemViewModel(new Playlist { Name = "聚合加载探针" });
        vm.Tracks.Clear();
        var rows = Enumerable.Range(1, 1000)
            .Select(index => new SongItemViewModel(
                new Song { Id = index, Name = $"Song {index}", DurationMs = 180_000 },
                _ => Task.FromResult(true), index))
            .ToList();
        vm.Tracks.AddRange(rows);

        var view = new PlaylistView { DataContext = vm };
        var window = new Window { Width = 1000, Height = 700, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
        var panel = view.GetVisualDescendants().OfType<VirtualizingStackPanel>().Single();
        var realized = panel.Children.Count;
        var loadingVisible = view.GetVisualDescendants().OfType<TextBlock>()
            .Any(text => text.Text == "正在加载更多歌曲…" && text.IsVisible);
        window.Close();
        Console.WriteLine($"[aggregate-load] realized={realized}/1000, loadingVisible={loadingVisible}");
        return realized > 0 && realized < 1000 && loadingVisible;
    }

    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(new HttpRequestException("offline probe"));
    }
}
