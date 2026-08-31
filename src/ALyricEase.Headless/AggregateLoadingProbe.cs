using System.Collections.Specialized;
using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.ViewModels;
using ALyricEase.Views;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ALyricEase.Headless;

/// <summary>聚合歌单流式加载的无网络回归：稳定来源排序、批尺寸和单次范围通知。</summary>
internal static class AggregateLoadingProbe
{
    public static int Run()
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
        Console.WriteLine($"[aggregate-load] stableOrder={stableOrder}, rangeNotification={ranged}, " +
                          $"liveCount={liveCount}, batchSizes={batchSizes}, virtualized={virtualized}");
        return stableOrder && ranged && liveCount && batchSizes && virtualized ? 0 : 1;
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
}
