using System.Net;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.Services.Audio;

namespace ALyricEase.Headless;

/// <summary>实际收听门槛、切歌取消/同曲重试与容量预检的回归；全部使用独立临时缓存。</summary>
internal static class AudioCachePolicyProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private const int Mb = 1024 * 1024;

    public static async Task VerifyAsync(string root)
    {
        await VerifyPlaybackSessionAsync();
        await VerifyCancellationAndRetryAsync(Path.Combine(root, "cancel-retry"));
        await VerifyQueuedCancellationAsync(Path.Combine(root, "cancel-queued"));
        await VerifyEvictionAsync(Path.Combine(root, "eviction"));
        await VerifyFullCacheUpgradeAsync(Path.Combine(root, "full-upgrade"));
        Console.WriteLine("[audio-cache-policy] PASS: 实播20秒/暂停/切歌取消/同曲重试/下载排队取消；空闲0扫描、满容量1扫描、LRU与播放保护正常");
    }

    private static async Task VerifyPlaybackSessionAsync()
    {
        var time = new ManualTimeProvider();
        using var session = new PlaybackCacheSession(time);
        var calls = 0;
        Task Download(CancellationToken _) { Interlocked.Increment(ref calls); return Task.CompletedTask; }

        session.Begin(Download, isPlaying: false);
        time.Advance(60);
        session.Tick();
        Check(session.DownloadTask is null, "加载时间提前触发音频缓存");
        session.UpdatePlaying(true);
        time.Advance(5);
        session.Tick();
        session.UpdatePlaying(false);
        time.Advance(60);
        session.Tick();
        Check(session.DownloadTask is null, "暂停时间计入了音频缓存门槛");
        session.UpdatePlaying(true);
        time.Advance(14.9);
        // Seek 会产生进度事件，但不应让实际收听时间凭空增加。
        for (var index = 0; index < 100; index++) session.Tick();
        Check(session.DownloadTask is null, "未累计播放20秒就开始缓存");
        time.Advance(0.1);
        session.Tick();
        await (session.DownloadTask ?? throw new InvalidOperationException("20秒后未启动缓存")).WaitAsync(Timeout);
        time.Advance(60);
        session.Tick();
        Check(calls == 1, "同次播放重复启动缓存");

        session.Begin(Download, isPlaying: true);
        time.Advance(3);
        session.Cancel();
        time.Advance(60);
        session.Tick();
        Check(session.DownloadTask is null && calls == 1, "快速切歌后仍启动缓存");

        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Begin(async token =>
        {
            started.TrySetResult(token);
            await Task.Delay(System.Threading.Timeout.Infinite, token);
        }, isPlaying: true);
        time.Advance(20);
        session.Tick();
        var pending = session.DownloadTask!;
        var downloadToken = await started.Task.WaitAsync(Timeout);
        session.Cancel();
        await pending.WaitAsync(Timeout);
        Check(downloadToken.IsCancellationRequested, "切歌未取消已启动的后台下载");

        session.Begin(Download, isPlaying: true);
        time.Advance(19);
        session.Dispose();
        time.Advance(60);
        session.Tick();
        Check(session.DownloadTask is null && calls == 1, "播放器释放后仍启动缓存");
    }

    private static async Task VerifyCancellationAndRetryAsync(string root)
    {
        var stream = new BlockingAudioStream();
        using var handler = new SequenceHandler(stream);
        using var http = new HttpClient(handler);
        var cache = new MusicCacheService(128, root, http);
        var song = ToSong(100);
        using var firstCancellation = new CancellationTokenSource();
        using var retryCancellation = new CancellationTokenSource();
        var first = cache.CacheAsync(song, "standard", "https://example.test/audio.mp3", firstCancellation.Token);
        Task joined = Task.CompletedTask;
        Task retry = Task.CompletedTask;
        try
        {
            await stream.WaitingRead.Task.WaitAsync(Timeout);
            Check(Directory.EnumerateFiles(root, "*.part").Any(), "下载未产生临时文件，取消场景未覆盖");
            joined = cache.CacheAsync(song, "standard", "https://example.test/audio.mp3", retryCancellation.Token);
            Check(handler.RequestCount == 1 && !joined.IsCompleted, "同曲并发请求未共享下载");
            firstCancellation.Cancel();
            await stream.CancellationObserved.Task.WaitAsync(Timeout);
            retry = cache.CacheAsync(song, "standard", "https://example.test/audio.mp3", retryCancellation.Token);
            Check(!retry.IsCompleted, "切回同曲时复用了已取消的下载结果");
            stream.FinishCancellation.TrySetResult();
            await Task.WhenAll(first, joined, retry).WaitAsync(Timeout);
            Check(handler.RequestCount == 2 && cache.IsAudioCached(song), "取消后同一首歌未重新下载成功");
            Check(stream.Disposed && !Directory.EnumerateFiles(root, "*.part").Any(), "取消下载后网络流或临时文件未清理");
            using var lease = cache.TryAcquireBestAvailable(song);
            Check(lease is not null && File.ReadAllBytes(lease.FilePath).SequenceEqual(SequenceHandler.Payload),
                "重试后缓存了不完整音频");
        }
        finally
        {
            firstCancellation.Cancel();
            retryCancellation.Cancel();
            stream.FinishCancellation.TrySetResult();
            await Task.WhenAll(first, joined, retry).WaitAsync(Timeout);
        }
    }

    private static async Task VerifyQueuedCancellationAsync(string root)
    {
        var firstStream = new BlockingAudioStream();
        var secondStream = new BlockingAudioStream();
        using var handler = new SequenceHandler(firstStream, secondStream);
        using var http = new HttpClient(handler);
        var cache = new MusicCacheService(128, root, http);
        using var activeCancellation = new CancellationTokenSource();
        using var queuedCancellation = new CancellationTokenSource();
        var first = cache.CacheAsync(ToSong(201), "standard", "https://example.test/1.mp3", activeCancellation.Token);
        var second = cache.CacheAsync(ToSong(202), "standard", "https://example.test/2.mp3", activeCancellation.Token);
        try
        {
            await Task.WhenAll(firstStream.WaitingRead.Task, secondStream.WaitingRead.Task).WaitAsync(Timeout);
            var queued = cache.CacheAsync(ToSong(203), "standard", "https://example.test/3.mp3", queuedCancellation.Token);
            queuedCancellation.Cancel();
            await queued.WaitAsync(Timeout);
            Check(handler.RequestCount == 2, "排队取消的歌曲仍发出了HTTP下载");
            activeCancellation.Cancel();
            firstStream.FinishCancellation.TrySetResult();
            secondStream.FinishCancellation.TrySetResult();
            await Task.WhenAll(first, second).WaitAsync(Timeout);
            Check(!Directory.EnumerateFiles(root, "*.part").Any(), "并发取消后残留临时文件");
            await cache.CacheAsync(ToSong(203), "standard", "https://example.test/3.mp3");
            Check(cache.IsAudioCached(ToSong(203)), "取消后下载并发槽未释放");
        }
        finally
        {
            activeCancellation.Cancel();
            queuedCancellation.Cancel();
            firstStream.FinishCancellation.TrySetResult();
            secondStream.FinishCancellation.TrySetResult();
            await Task.WhenAll(first, second).WaitAsync(Timeout);
        }
    }

    private static async Task VerifyEvictionAsync(string root)
    {
        using var http = new HttpClient(new SizedHandler());
        var cache = new MusicCacheService(128, root, http);
        await cache.SetMaximumSizeMbAsync(128); // 校准容量，隔离启动裁剪的扫描。
        var scans = cache.EvictionScanCount;
        var playing = ToSong(301);
        await cache.CacheAsync(playing, "standard", "https://example.test/1.mp3");
        Check(cache.EvictionScanCount == scans, "空间充足时音频写入仍扫描全目录");
        using var lease = cache.TryAcquireBestAvailable(playing)
                          ?? throw new InvalidOperationException("无法固定播放缓存");
        var older = Path.Combine(root, "old.cover");
        var newer = Path.Combine(root, "new.cover");
        CreateSizedFile(older, 80L * Mb);
        CreateSizedFile(newer, 47L * Mb);
        File.SetLastWriteTimeUtc(lease.FilePath, DateTime.UtcNow.AddDays(-3));
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(newer, DateTime.UtcNow.AddDays(-1));
        cache.GetCurrentSizeBytes();
        scans = cache.EvictionScanCount;
        var incoming = ToSong(302);
        await cache.CacheAsync(incoming, "standard", "https://example.test/8.mp3");
        Check(cache.EvictionScanCount - scans == 1, "满容量音频写入重复扫描目录");
        Check(!File.Exists(older) && File.Exists(newer) && File.Exists(lease.FilePath)
              && cache.IsAudioCached(incoming), "LRU淘汰顺序或播放保护错误");
        Check(cache.GetCurrentSizeBytes() == 56L * Mb, "满容量淘汰后的容量记账错误");

        using (var low = cache.TryAcquireBestAvailable(incoming))
        {
            var lowPath = low!.FilePath;
            await cache.CacheAsync(incoming, "lossless", "https://example.test/12.flac");
            Check(File.Exists(lowPath), "音质升级删除了仍在播放的低音质文件");
            low.Dispose();
            Check(!File.Exists(lowPath), "低音质租约释放后未删除旧文件");
        }
        Check(cache.GetCurrentSizeBytes() == 60L * Mb, "音质升级后的容量记账错误");

        // 播放中的音频占满全部配额时，拒绝新写入，保留现有可用缓存。
        File.Delete(newer);
        using (var high = cache.TryAcquireBestAvailable(incoming))
            File.Delete(high!.FilePath);
        CreateSizedFile(lease.FilePath, 128L * Mb);
        var retained = Path.Combine(root, "retained.cover");
        CreateSizedFile(retained, 1024);
        cache.GetCurrentSizeBytes();
        scans = cache.EvictionScanCount;
        await cache.CacheAsync(playing, "lossless", "https://example.test/8.flac");
        Check(cache.EvictionScanCount - scans == 1 && File.Exists(lease.FilePath)
              && File.Exists(retained), "无法容纳新音频时仍破坏了原缓存");
        using var stillLow = cache.TryAcquireBestAvailable(playing);
        Check(stillLow?.QualityRank == 1 && !Directory.EnumerateFiles(root, "*.part").Any(),
            "容量不足时错误提交了升级音频或残留临时文件");
    }

    private static void CreateSizedFile(string path, long length)
    {
        using var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
        file.SetLength(length);
    }

    private static async Task VerifyFullCacheUpgradeAsync(string root)
    {
        using var http = new HttpClient(new SizedHandler());
        var cache = new MusicCacheService(128, root, http);
        var song = ToSong(401);
        await cache.CacheAsync(song, "standard", "https://example.test/8.mp3");
        var oldPath = Directory.EnumerateFiles(root, "a-*").Single();
        var retained = Path.Combine(root, "retained.cover");
        CreateSizedFile(retained, 120L * Mb);
        cache.GetCurrentSizeBytes();
        var scans = cache.EvictionScanCount;
        // 新文件虽然放不下，但先回收本曲的旧版本后足够；不应额外淘汰其他内容。
        await cache.CacheAsync(song, "lossless", "https://example.test/4.flac");
        Check(cache.EvictionScanCount - scans == 1 && !File.Exists(oldPath) && File.Exists(retained)
              && cache.GetCurrentSizeBytes() == 124L * Mb, "满容量升级未复用旧音频释放的空间");

        scans = cache.EvictionScanCount;
        await cache.CacheAsync(song, "hires", "https://example.test/12.flac");
        using var upgraded = cache.TryAcquireBestAvailable(song);
        Check(cache.EvictionScanCount - scans == 1 && !File.Exists(retained)
              && upgraded?.QualityRank == 5 && cache.GetCurrentSizeBytes() == 12L * Mb,
            "满容量升级时淘汰或容量记账错误");
    }

    private static Song ToSong(long id) => new() { Id = id, Source = MusicSource.NetEase, Name = "缓存策略探针" };

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("[audio-cache-policy] " + message);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(double seconds) => _timestamp += (long)Math.Round(seconds * TimeSpan.TicksPerSecond);
    }

    private sealed class SequenceHandler(params BlockingAudioStream[] streams) : HttpMessageHandler
    {
        internal static readonly byte[] Payload = [1, 2, 3, 4, 5];
        private int _requests;
        public int RequestCount => Volatile.Read(ref _requests);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var index = Interlocked.Increment(ref _requests) - 1;
            HttpContent content = index < streams.Length
                ? new StreamContent(streams[index])
                : new ByteArrayContent(Payload);
            content.Headers.ContentType = new("audio/mpeg");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class SizedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var size = int.Parse(Path.GetFileNameWithoutExtension(request.RequestUri!.AbsolutePath));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[size * Mb]),
            });
        }
    }

    private sealed class BlockingAudioStream : Stream
    {
        public TaskCompletionSource WaitingRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FinishCancellation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        private bool _firstRead = true;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_firstRead)
            {
                _firstRead = false;
                buffer.Span[..1024].Fill(7);
                return 1024;
            }
            WaitingRead.TrySetResult();
            try { await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException)
            {
                CancellationObserved.TrySetResult();
                await FinishCancellation.Task.WaitAsync(Timeout);
                throw;
            }
            return 0;
        }

        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
