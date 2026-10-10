using System.Collections.Specialized;
using System.Net;
using System.Reflection;
using System.Text.Json;
using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.Services.NetEase;
using ALyricEase.ViewModels;
using ALyricEase.Views;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ALyricEase.Headless;

/// <summary>隔离网络验证资料页快照/批量通知，以及推荐区块刷新和迟到请求。</summary>
internal static class PageRefreshProbe
{
    public static async Task RunAsync()
    {
        var main = QqUserFailureProbe.CreateOfflineMain(Path.Combine(Path.GetTempPath(), $"aly-page-refresh-{Guid.NewGuid():N}"));
        var api = ServiceLocator.Get<NetEaseApiClient>();
        var handler = new ResponseHandler();
        var httpField = typeof(NetEaseApiClient).GetField("_http", BindingFlags.Instance | BindingFlags.NonPublic)!;
        ((HttpClient)httpField.GetValue(api)!).Dispose();
        httpField.SetValue(api, new HttpClient(handler));
        await VerifyProfileAsync(main.UserProfile, api, handler);
        await VerifyRecommendAsync(ServiceLocator.Get<RecommendViewModel>(), api, handler);
        Console.WriteLine("[page-refresh] PASS");
    }

    private static async Task VerifyProfileAsync(UserProfileViewModel vm, NetEaseApiClient api, ResponseHandler handler)
    {
        var adds = 0;
        vm.CreatedPlaylists.CollectionChanged += (_, e) => { if (e.Action == NotifyCollectionChangedAction.Add) adds++; };
        var view = new UserProfileView { DataContext = vm };
        var window = new Window { Width = 1000, Height = 720, Content = view };
        window.Show();
        await vm.LoadAsync(42);
        Dispatcher.UIThread.RunJobs();
        Check(vm.CreatedPlaylists.Count == 120 && vm.TasteRows.Count == 1 && vm.CollectedPlaylists.Count == 1,
            "用户资料页三组内容完整");
        Check(adds == 1 && !vm.IsLoading, "120 个自建歌单仅发出一次追加通知");
        vm.CreatedPlaylists[0].UpdatePlayCount(12345);
        vm.CreatedPlaylists[0].UpdateTrackCount(88);
        vm.UpdatePageScrollOffset(340);
        var requests = handler.ProfileRequests;
        var snapshot = vm.CaptureAndReleaseNavigationSnapshot()!;
        Check(!vm.HasRetainedPageData && vm.CreatedPlaylists.Count == 0, "离页释放用户页数据");
        await vm.RestoreNavigationSnapshotAsync(snapshot);
        Check(handler.ProfileRequests == requests && vm.CreatedPlaylists.Count == 120 && vm.Nickname == "资料探针",
            "返回资料页零网络请求");
        Check(vm.PageScrollOffset == 340 && vm.CreatedPlaylists[0].TrackCount == 88
            && vm.CreatedPlaylists[0].CurrentPlayCount == 12345 && vm.CreatedPlaylists[0].CreatorId == 42
            && vm.TasteRows[0].ShowHeart, "缓存恢复滚动、角标、创建者和音乐品味");
        Check(await ServiceLocator.Get<MusicCacheService>().TryTakeDetailPageSnapshotAsync(snapshot.CacheKey) is null,
            "快照读取后移除");
        snapshot = vm.CaptureAndReleaseNavigationSnapshot()!;
        vm.SyncCreatedPlaylist(new Playlist { Id = 1, Name = "删除歌单" }, null);
        await vm.RestoreNavigationSnapshotAsync(snapshot);
        Check(handler.ProfileRequests == requests + 2, "歌单编辑后返回重新请求");
        requests = handler.ProfileRequests;
        snapshot = vm.CaptureAndReleaseNavigationSnapshot()!;
        ChangeAccountGeneration(api);
        await vm.RestoreNavigationSnapshotAsync(snapshot);
        Check(handler.ProfileRequests == requests + 2, "账号代次变化后快照失效");
        snapshot = vm.CaptureAndReleaseNavigationSnapshot()!;
        vm.DiscardNavigationSnapshot(snapshot);
        Check(await ServiceLocator.Get<MusicCacheService>().TryTakeDetailPageSnapshotAsync(snapshot.CacheKey) is null,
            "丢弃返回栈时清理用户快照");
        window.Close();
    }

    private static async Task VerifyRecommendAsync(RecommendViewModel vm, NetEaseApiClient api, ResponseHandler handler)
    {
        typeof(RecommendViewModel).GetField("_loading", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, false);
        var card = new RecommendCardViewModel("原歌单", "原说明", id: 1);
        vm.ApplySection("推荐歌单", [card]);
        vm.ApplySection("热门歌曲", [new RecommendCardViewModel("旧热歌", "", id: 2)]);
        vm.ApplySection("猜你喜欢", [new RecommendCardViewModel("旧新歌", "", id: 3)]);
        var section = vm.Sections[0];
        var sectionChanges = 0;
        var itemChanges = 0;
        vm.Sections.CollectionChanged += (_, _) => sectionChanges++;
        section.Items.CollectionChanged += (_, _) => itemChanges++;
        var view = new RecommendView { DataContext = vm };
        var window = new Window { Width = 1200, Height = 900, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var images = view.GetVisualDescendants().OfType<Image>().ToArray();
        await vm.EnsureLoadedAsync();
        Dispatcher.UIThread.RunJobs();
        Check(sectionChanges == 0 && itemChanges == 0 && ReferenceEquals(section.Items[0], card),
            "推荐更新复用区块和卡片，集合变更为零");
        Check(card.Title == "更新歌单" && card.PlayCount == 99, "复用卡片更新标题和播放量");
        var retained = images.Count(view.GetVisualDescendants().OfType<Image>().Contains);
        Check(images.Length > 0 && retained == images.Length, $"推荐图片控件保留 {retained}/{images.Length}");
        Check(vm.Sections.Any(s => s.Title == "热门歌曲") && vm.Sections.Any(s => s.Title == "猜你喜欢"),
            "HTTP 成功但业务码失败时保留旧区块");

        handler.PlaylistPending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.NewSongsEmpty = true;
        var refresh = vm.EnsureLoadedAsync();
        await WaitUntilAsync(() => !vm.Sections.Any(s => s.Title == "猜你喜欢"));
        Check(!refresh.IsCompleted && vm.Sections.Contains(section), "慢区块不阻塞其他区块独立更新");
        // 请求尚未结束时换账号，旧账号的结果必须被丢弃，随后加载新账号。
        ChangeAccountGeneration(api);
        var pending = handler.PlaylistPending;
        handler.PlaylistPending = null;
        handler.PlaylistId = 4;
        pending.SetResult(ResponseHandler.Json("""{"code":200,"result":[{"id":999,"name":"迟到旧账号"}]}"""));
        await refresh;
        Check(section.Items.OfType<RecommendCardViewModel>().Single().Id == 4,
            "切换账号后丢弃迟到结果并自动重新加载");
        Check(vm.Sections.Select(s => s.Title).SequenceEqual(new[] { "推荐歌单", "热门歌曲" }), "完成顺序不改变区块顺序");
        vm.ApplySection("每日歌曲推荐", [new RecommendCardViewModel("每日", "", id: 5)], true);
        Check(vm.Sections[0].Title == "每日歌曲推荐", "每日区块迟到时插入正确位置");
        window.Close();
    }

    private static void ChangeAccountGeneration(NetEaseApiClient api)
        => typeof(NetEaseApiClient).GetField("_accountGeneration", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(api, api.AccountGeneration + 1);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(10);
        Check(condition(), "异步区块按时完成");
    }

    private static void Check(bool passed, string name)
    {
        if (!passed) throw new InvalidOperationException($"[page-refresh] FAIL {name}");
        Console.WriteLine($"[page-refresh] PASS {name}");
    }

    private sealed class ResponseHandler : HttpMessageHandler
    {
        public int ProfileRequests;
        public long PlaylistId = 1;
        public bool NewSongsEmpty;
        public TaskCompletionSource<HttpResponseMessage>? PlaylistPending;

        internal static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("user/detail"))
            {
                ProfileRequests++;
                return Task.FromResult(Json("""{"code":200,"level":8,"profile":{"userId":42,"nickname":"资料探针","signature":"签名","follows":12,"followeds":34}}"""));
            }
            if (path.Contains("user/playlist"))
            {
                ProfileRequests++;
                var items = Enumerable.Range(1, 122).Select(i => new
                {
                    id = i, name = $"歌单{i}", trackCount = 20, playCount = 30,
                    specialType = i == 121 ? 5 : 0, creator = new { userId = i == 122 ? 43 : 42, nickname = "创建者" },
                });
                return Task.FromResult(Json(JsonSerializer.Serialize(new { code = 200, playlist = items })));
            }
            if (path.Contains("personalized/playlist"))
                return PlaylistPending?.Task ?? Task.FromResult(Json(JsonSerializer.Serialize(new
                {
                    code = 200, result = new[] { new { id = PlaylistId, name = "更新歌单", playCount = 99 } },
                })));
            if (path.Contains("personalized/newsong") && NewSongsEmpty)
                return Task.FromResult(Json("""{"code":200,"result":[]}"""));
            return Task.FromResult(Json("""{"code":500}"""));
        }
    }
}
