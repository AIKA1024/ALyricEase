using ALyricEase.Models;
using ALyricEase.Models.Dtos;
using ALyricEase.Services;
using ALyricEase.Services.NetEase;
using ALyricEase.ViewModels;

namespace ALyricEase.Headless;

/// <summary>版权、VIP 与单曲购买权益预判的纯内存回归。</summary>
public static class PlaybackAvailabilityProbe
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;

        Check(ref failures,
            !NetEaseApiClient.IsCopyrightUnavailable(new SongPrivilegeDto { St = -1 }),
            "st=-1 不应被提前判定为无版权");
        Check(ref failures,
            NetEaseApiClient.IsCopyrightUnavailable(new SongPrivilegeDto { St = -200 }),
            "只有 st=-200 应判定为明确无版权");
        Check(ref failures,
            !NetEaseApiClient.IsCopyrightUnavailable(null),
            "缺少 privilege 时应允许播放地址实测");
        Check(ref failures,
            NetEaseApiClient.GetPurchasedStatus(new SongPrivilegeDto { PurchaseStatus = 3 }) == true
            && NetEaseApiClient.GetPurchasedStatus(new SongPrivilegeDto { PurchaseStatus = 5 }) == true,
            "payed=3/5 应识别为已购买");
        Check(ref failures,
            NetEaseApiClient.GetPurchasedStatus(new SongPrivilegeDto { PurchaseStatus = 0 }) == false
            && NetEaseApiClient.GetPurchasedStatus(null) is null,
            "明确未购买与缺少购买字段应保持可区分");

        var loggedOut = new TestMusicApi(isLoggedIn: false, isVip: false, isVipLoaded: true);
        var nonVip = new TestMusicApi(isLoggedIn: true, isVip: false, isVipLoaded: true);
        var vip = new TestMusicApi(isLoggedIn: true, isVip: true, isVipLoaded: true);
        var loadingVip = new TestMusicApi(isLoggedIn: true, isVip: false, isVipLoaded: false);

        var vipSong = new Song { Source = MusicSource.NetEase, Id = 1, Name = "VIP 歌曲", Fee = 1 };
        Check(ref failures, !PlaybackAvailability.CanAttempt(vipSong, loggedOut),
            "VIP 歌曲在未登录时应提前禁用");
        Check(ref failures, !PlaybackAvailability.CanAttempt(vipSong, nonVip),
            "VIP 歌曲在已确认非会员时应提前禁用");
        Check(ref failures, PlaybackAvailability.CanAttempt(vipSong, vip),
            "VIP 歌曲在会员账号下应允许播放");
        Check(ref failures, PlaybackAvailability.CanAttempt(vipSong, loadingVip),
            "会员状态尚未加载时不应把默认 false 当成非会员");

        var normalQualityFree = new Song { Source = MusicSource.NetEase, Id = 2, Name = "普通音质免费", Fee = 8 };
        Check(ref failures, PlaybackAvailability.CanAttempt(normalQualityFree, loggedOut),
            "fee=8 应允许未登录用户尝试普通音质");

        var purchased = new Song { Source = MusicSource.NetEase, Id = 3, Name = "已购数字专辑", Fee = 4, IsPurchased = true };
        var notPurchased = new Song { Source = MusicSource.NetEase, Id = 4, Name = "未购数字专辑", Fee = 4, IsPurchased = false };
        var purchaseUnknown = new Song { Source = MusicSource.NetEase, Id = 5, Name = "购买状态未知", Fee = 4 };
        Check(ref failures, PlaybackAvailability.CanAttempt(purchased, nonVip),
            "已购买数字专辑应允许非会员账号播放");
        Check(ref failures, !PlaybackAvailability.CanAttempt(purchased, loggedOut),
            "付费歌曲即使残留已购状态，未登录时也应提前禁用");
        Check(ref failures, !PlaybackAvailability.CanAttempt(notPurchased, vip),
            "明确未购买的数字专辑即使是会员也应提前禁用");
        Check(ref failures, PlaybackAvailability.CanAttempt(purchaseUnknown, nonVip),
            "已登录但接口未提供购买状态时应保留播放地址实测机会");

        var copyrightBlocked = new Song
        {
            Source = MusicSource.NetEase,
            Id = 6,
            Name = "明确无版权",
            IsNoCopyright = true,
        };
        Check(ref failures, !PlaybackAvailability.CanAttempt(copyrightBlocked, vip),
            "明确无版权歌曲应保持禁用");

        var failedSong = new Song
        {
            Source = MusicSource.NetEase,
            Id = 7,
            Name = "播放地址为空",
            Fee = 0,
        };
        var row = new SongItemViewModel(failedSong, _ => Task.FromResult(false));
        Check(ref failures, row.IsPlayable, "免费歌曲在播放实测前应保持可点");
        await row.PlayCommand.ExecuteAsync(null);
        Check(ref failures, !row.IsPlayable, "播放地址确认失败后歌曲行应置灰");

        Console.WriteLine(failures == 0
            ? "[playability] PASS 播放权益回归全部通过"
            : $"[playability] FAIL {failures} 项失败");
        return failures == 0 ? 0 : 1;
    }

    private static void Check(ref int failures, bool condition, string message)
    {
        Console.WriteLine($"[playability] {(condition ? "PASS" : "FAIL")} {message}");
        if (!condition) failures++;
    }

    private sealed class TestMusicApi(bool isLoggedIn, bool isVip, bool isVipLoaded) : IMusicApi
    {
        public MusicSource Source => MusicSource.NetEase;
        public string DisplayName => "测试音源";
        public bool IsLoggedIn => isLoggedIn;
        public bool IsVip => isVip;
        public bool IsVipLoaded => isVipLoaded;
        public Task EnsureVipStatusAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<List<Song>> SearchAsync(string keyword, int limit = 30, int offset = 0,
            CancellationToken ct = default) => Task.FromResult(new List<Song>());
        public Task<PlayUrlItem?> GetPlayUrlAsync(Song song, string level = "higher",
            CancellationToken ct = default) => Task.FromResult<PlayUrlItem?>(null);
        public Task<LyricResult?> GetLyricAsync(Song song, CancellationToken ct = default)
            => Task.FromResult<LyricResult?>(null);
        public Task<Song?> GetSongDetailAsync(long id, CancellationToken ct = default)
            => Task.FromResult<Song?>(null);
    }
}
