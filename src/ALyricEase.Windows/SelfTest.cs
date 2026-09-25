using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.Services.Audio;
using ALyricEase.Services.Auth;
using ALyricEase.Services.Crypto;
using ALyricEase.Services.NetEase;
using ALyricEase.Services.QQMusic;
using ALyricEase.Services.Smtc;
using ALyricEase.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace ALyricEase;

/// <summary>M1 验证入口:命令行跑 --selftest 时执行,打印加密/匿名/搜索/播放地址/歌词结果到控制台。</summary>
internal static class SelfTest
{
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern System.IntPtr CreateWindowExW(
        uint exStyle, string className, string? windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern System.Boolean ShowWindow(System.IntPtr hWnd, int nCmdShow);
    /// <summary>内存泄漏自测:连续播放多首,报告 GC/进程内存,定位是否按播放线性增长。</summary>
    public static async Task RunLeakTestAsync()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var services = new ServiceCollection();
        services.AddSingleton<DispatcherService>();
        services.AddSingleton<CookieStore>();
        services.AddSingleton<CnIpPool>();
        services.AddSingleton<CryptoService>();
        services.AddSingleton<NetEaseApiClient>();
        services.AddSingleton<QQMusicApiClient>();
        services.AddSingleton<IMusicApi>(sp => sp.GetRequiredService<NetEaseApiClient>());
        services.AddSingleton<IMusicApi>(sp => sp.GetRequiredService<QQMusicApiClient>());
        services.AddSingleton<MusicApiProvider>();
#if ANDROID
        services.AddSingleton<IAudioPlayer, AndroidMediaPlayer>();
        services.AddSingleton<ISmtcService, SmtcServiceStub>();
#else
        services.AddSingleton<IAudioPlayer, WindowsMediaPlayer>();
        services.AddSingleton<ISmtcService, SmtcService>();
#endif
        services.AddSingleton<LyricViewModel>();
        services.AddSingleton<PlayerViewModel>();
        var sp = services.BuildServiceProvider(); // 无头 selftest:不 DisposeAsync(WinRT MediaPlayer 无泵 Dispose 会挂)

        var api = sp.GetRequiredService<NetEaseApiClient>();
        var player = sp.GetRequiredService<PlayerViewModel>();
        var audio = sp.GetRequiredService<IAudioPlayer>();
        var p = System.Diagnostics.Process.GetCurrentProcess();

        long GcMb() => GC.GetTotalMemory(false) / 1024 / 1024;
        long WsMb() => p.WorkingSet64 / 1024 / 1024;

        Console.WriteLine("[leak] 搜索并挑可播歌曲...");
        var songs = await api.SearchAsync("周杰伦", 30);
        var playable = new List<Song>();
        foreach (var s in songs)
        {
            var u = await api.GetPlayUrlAsync(s.Id, "standard");
            if (u?.Url is not null) playable.Add(s);
            if (playable.Count >= 8) break;
        }
        Console.WriteLine($"[leak] 可播 {playable.Count} 首,初始 GC={GcMb()}MB WS={WsMb()}MB");

        for (var i = 0; i < playable.Count; i++)
        {
            await player.PlayAsync(playable[i]);
            await Task.Delay(2500);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var name = playable[i].Name;
            if (name.Length > 12) name = name[..12];
            Console.WriteLine($"[leak] 播放#{i + 1} {name} GC={GcMb()}MB WS={WsMb()}MB");
        }
        Console.WriteLine($"[leak] 连播后 GC={GcMb()}MB WS={WsMb()}MB");

        // 模拟打开歌单:全量曲目仅保存封面 URL，未创建 Image → 不应暴涨
        Console.WriteLine("[leak] 打开歌单(懒封面不拉图)...");
        var plSongs = await api.GetPlaylistDetailAsync(19723756);
        var items = new List<SongItemViewModel>();
        foreach (var s in plSongs)
            items.Add(new SongItemViewModel(s, _ => System.Threading.Tasks.Task.FromResult(true)));
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Console.WriteLine($"[leak] 歌单 {plSongs.Count} 首, 建 VM(不拉封面) GC={GcMb()}MB WS={WsMb()}MB");

        // 单曲长播:区分按时间累积的泄漏(播放/渲染/缓冲)
        Console.WriteLine("[leak] 单曲长播 60s 监控(每 5s)...");
        await player.PlayAsync(playable[0]);
        for (var s = 0; s < 60; s += 5)
        {
            await Task.Delay(5000);
            GC.Collect();
            Console.WriteLine($"[leak] 长播 +{s + 5}s GC={GcMb()}MB WS={WsMb()}MB");
        }
        player.Dispose();
    }

    /// <summary>SMTC 无头验证:真实窗口句柄 + Initialize,报告 WinRT 互操作是否建立、
    /// 元数据推送是否可调。背景: f3b0a3e 关闭 MediaPlayer 自动集成后 SMTC 全靠
    /// SmtcService 手动驱动 —— 互操作一旦失败,系统将完全没有媒体会话(静默降级难察觉)。</summary>
    public static async Task RunSmtcProbeAsync()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        // 后台/重定向环境没有控制台窗口(hwnd=0):建一个真实顶层窗口供 GetForWindow 关联
        var hwnd = CreateWindowExW(0, "STATIC", "ALyricEase smtc probe", 0x00CF0000 /*WS_OVERLAPPEDWINDOW*/,
            0, 0, 200, 100, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        ShowWindow(hwnd, 5 /*SW_SHOW*/);
        Console.WriteLine($"[smtc] hwnd: 0x{hwnd:X}");
        var services = new ServiceCollection();
        services.AddSingleton<DispatcherService>();
        services.AddSingleton<IAudioPlayer, WindowsMediaPlayer>();
        services.AddSingleton<ISmtcService, SmtcService>();
        await using var sp = services.BuildServiceProvider();
        var smtc = (SmtcService)sp.GetRequiredService<ISmtcService>();
        smtc.Initialize(hwnd);
        Console.WriteLine($"[smtc] IsAvailable: {smtc.IsAvailable}");
        if (smtc.IsAvailable)
        {
            smtc.SetNowPlaying("SMTC 探针测试曲", "探针艺术家", "ALyricEase", "");
            Console.WriteLine("[smtc] SetNowPlaying: 已调用(无异常)");
            Thread.Sleep(1500); // 交互环境下留出观察系统音量浮层的时间
        }
    }

    /// <summary>创建者显示探针:用真实 Cookie 走 OpenPlaylistAsync 完整链路(个性推荐→歌单详情),
    /// 打印 UserName / CreatorName / 接口侧创建者,定位"推荐歌单作者显示为自己"的问题。</summary>
    public static async Task RunCreatorProbeAsync()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var services = new ServiceCollection();
        services.AddSingleton<DispatcherService>();
        services.AddSingleton<AppStateStore>();
        services.AddSingleton<MusicCacheService>();
        services.AddSingleton<CookieStore>();
        services.AddSingleton<CnIpPool>();
        services.AddSingleton<CryptoService>();
        services.AddSingleton<NetEaseApiClient>();
        services.AddSingleton<QQMusicApiClient>();
        services.AddSingleton<IMusicApi>(sp => sp.GetRequiredService<NetEaseApiClient>());
        services.AddSingleton<IMusicApi>(sp => sp.GetRequiredService<QQMusicApiClient>());
        services.AddSingleton<MusicApiProvider>();
        services.AddSingleton<IAudioPlayer, WindowsMediaPlayer>();
        services.AddSingleton<ISmtcService, SmtcServiceStub>();
        services.AddSingleton<LyricViewModel>();
        services.AddSingleton<PlayerViewModel>();
        services.AddSingleton<PlaylistViewModel>();
        var sp = services.BuildServiceProvider();

        var api = sp.GetRequiredService<NetEaseApiClient>();
        var vm = sp.GetRequiredService<PlaylistViewModel>();

        var personalized = await api.GetPersonalizedPlaylistsAsync(2);
        Console.WriteLine($"[creator] 推荐歌单 {personalized.Count} 个;UserName={vm.UserName}");
        foreach (var item in personalized)
        {
            var pvm = new PlaylistItemViewModel(new Playlist
            {
                Id = item.Id, Name = item.Title, CoverUrl = item.CoverUrl, TrackCount = item.TrackCount,
            });
            await vm.OpenPlaylistCommand.ExecuteAsync(pvm);
            var direct = await api.GetPlaylistTrackOverviewAsync(item.Id);
            Console.WriteLine(
                $"[creator] {item.Title} | UserName={vm.UserName} | CreatorName={vm.CreatorName} | 接口侧创建者={direct.CreatorNickname}");
        }
    }

    /// <summary>M2 无头播放验证:真实拉流第一条搜索结果,确认 Playing 状态到达且进度前进。</summary>
    public static async Task RunPlayAsync()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var services = new ServiceCollection();
        services.AddSingleton<DispatcherService>();
        services.AddSingleton<CookieStore>();
        services.AddSingleton<CnIpPool>();
        services.AddSingleton<CryptoService>();
        services.AddSingleton<NetEaseApiClient>();
        services.AddSingleton<QQMusicApiClient>();
        services.AddSingleton<IMusicApi>(sp => sp.GetRequiredService<NetEaseApiClient>());
        services.AddSingleton<IMusicApi>(sp => sp.GetRequiredService<QQMusicApiClient>());
        services.AddSingleton<MusicApiProvider>();
#if ANDROID
        services.AddSingleton<IAudioPlayer, AndroidMediaPlayer>();
        services.AddSingleton<ISmtcService, SmtcServiceStub>();
#else
        services.AddSingleton<IAudioPlayer, WindowsMediaPlayer>();
        services.AddSingleton<ISmtcService, SmtcService>();
#endif
        services.AddSingleton<LyricViewModel>();
        services.AddSingleton<PlayerViewModel>();
        var sp = services.BuildServiceProvider(); // 无头 selftest:不 DisposeAsync(WinRT MediaPlayer 在无泵线程 Dispose 会挂)

        var api = sp.GetRequiredService<NetEaseApiClient>();
        var player = sp.GetRequiredService<PlayerViewModel>();
        var audio = sp.GetRequiredService<IAudioPlayer>();
        var lyric = sp.GetRequiredService<LyricViewModel>();

        try
        {
            Console.WriteLine("[play] 搜索:优先选「有时间轴歌词 + 可播放」的歌...");
            Console.Out.Flush();
            var songs = await api.SearchAsync("周杰伦 晴天", 10);
            if (songs.Count == 0) { Console.WriteLine("[play] FAIL 无搜索结果"); return; }

            Song? lyricSong = null;
            foreach (var s in songs)
            {
                var lrc = await api.GetLyricAsync(s.Id);
                if (lrc is null || Services.Lrc.LrcParser.Parse(lrc.Original, lrc.Translation).Lines.Count == 0)
                    continue;
                var url = await api.GetPlayUrlAsync(s.Id, "higher");
                if (url?.Url is not null) { lyricSong = s; break; }
            }
            var song = lyricSong ?? songs[0];
            Console.WriteLine($"[play] 播放: {song.Id} {song.Name} - {song.Artist}");
            Console.Out.Flush();

            await player.PlayAsync(song);
            for (var i = 0; i < 40; i++)
            {
                await Task.Delay(250);
                // 事件泵在 Program.Main 的主线程 RunJobs 循环里完成,selftest 体只轮询属性。
                if (player.IsPlaying && player.PositionMs > 0 && player.DurationMs > 0)
                {
                    Console.WriteLine($"[play] OK 正在播放 pos={player.PositionMs}ms dur={player.DurationMs}ms");
                    player.TogglePlayPauseCommand.Execute(null); // 暂停
                    await Task.Delay(500);
                    Console.WriteLine($"[play] 暂停后 IsPlaying={player.IsPlaying} pos={player.PositionMs}ms");
                    player.TogglePlayPauseCommand.Execute(null); // 恢复
                    await Task.Delay(500);
                    Console.WriteLine($"[play] 恢复后 IsPlaying={player.IsPlaying}");

                    // 音量:VM 写入 → 音频层生效
                    player.Volume = 50;
                    await Task.Delay(200);
                    Console.WriteLine($"[play] 音量 VM={player.Volume} 底层={audio.Volume} " +
                                      $"{(audio.Volume == 50 ? "OK" : "FAIL")}");

                    // 拖动 seek:VM 置位 → BeginScrub/EndScrub → 音频跳转
                    player.PositionMs = 30000;
                    player.BeginScrub();
                    player.EndScrub();
                    await Task.Delay(800);
                    var seekOk = audio.PositionMs >= 25000;
                    Console.WriteLine($"[play] 拖动后底层 pos={audio.PositionMs}ms {(seekOk ? "OK" : "FAIL")}");

                    // 歌词联动:PlayAsync 触发的 LoadAsync 应已解析出时间轴歌词,且当前句已定位
                    for (var w = 0; w < 16 && !lyric.HasLyric; w++)
                    {
                        await Task.Delay(250);
                    }
                    var lyricOk = lyricSong is not null && lyric.HasLyric && lyric.Lines.Count > 0
                                  && lyric.CurrentIndex >= 0;
                    Console.WriteLine($"[play] 歌词联动 HasLyric={lyric.HasLyric} 行数={lyric.Lines.Count} " +
                                      $"当前句@{lyric.CurrentIndex} {(lyricOk ? "OK" : (lyricSong is null ? "SKIP(无歌词曲)" : "FAIL"))}");

                    Console.WriteLine(seekOk && audio.Volume == 50 && lyricOk
                        ? "[play] M2+M3 全链路验证通过"
                        : "[play] 部分验证未通过");
                    Console.Out.Flush();
                    return;
                }
                if (player.Message is { } msg && !player.IsLoading)
                {
                    Console.WriteLine($"[play] FAIL 播放失败: {msg}");
                    Console.Out.Flush();
                    return;
                }
            }
            Console.WriteLine($"[play] FAIL 10s 内未进入播放 state 消息={player.Message}");
            Console.Out.Flush();
        }
        finally
        {
            player.Dispose();
        }
    }

    /// <summary>音频输出设备真机自测:枚举→切换→无效 Id 拒绝→null 复位。
    /// 需要真实音频子系统(至少一块渲染设备);不联网不播流,只验证 WinRT 设备语义。</summary>
    public static async Task RunAudioDeviceTestAsync()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var services = new ServiceCollection();
        services.AddSingleton<DispatcherService>();
        await using var sp = services.BuildServiceProvider();
        var player = new WindowsMediaPlayer(sp.GetRequiredService<DispatcherService>());

        var devices = await player.RefreshOutputDevicesAsync();
        Console.WriteLine($"[audiodev] 枚举到 {devices.Count} 个输出设备:");
        foreach (var d in devices)
            Console.WriteLine($"  {(d.IsDefault ? "*" : " ")} {d.Name}  id={d.Id[..Math.Min(48, d.Id.Length)]}...");

        var ok = devices.Count > 0;
        Console.WriteLine(ok ? "[audiodev] 枚举 OK" : "[audiodev] FAIL 未枚举到任何渲染设备");

        if (ok)
        {
            // 切到第一个真实设备:必须成功且回读一致
            var first = devices[0];
            var switchOk = player.TrySetOutputDevice(first.Id)
                           && player.OutputDeviceId == first.Id;
            Console.WriteLine($"[audiodev] 切到「{first.Name}」 {(switchOk ? "OK" : "FAIL")}");
            ok &= switchOk;

            // 无效 Id(已拔出/伪造):必须拒绝且当前设备不变
            var rejectOk = !player.TrySetOutputDevice("{0.0.0.00000000}.{DEADBEEF-0000-0000-0000-000000000000}")
                           && player.OutputDeviceId == first.Id;
            Console.WriteLine($"[audiodev] 无效 Id 拒绝 {(rejectOk ? "OK" : "FAIL")}");
            ok &= rejectOk;

            // null = 回系统默认:必须成功且回读为空
            var resetOk = player.TrySetOutputDevice(null) && player.OutputDeviceId is null;
            Console.WriteLine($"[audiodev] null 复位 {(resetOk ? "OK" : "FAIL")}");
            ok &= resetOk;
        }

        Console.WriteLine(ok ? "[audiodev] 真机自测全部通过" : "[audiodev] 真机自测存在 FAIL");
        Environment.ExitCode = ok ? 0 : 1;
    }

    /// <summary>交叉淡化真机自测:生成两段不同频率的正弦 WAV,验证
    /// ① 单实例正常播放;② CrossfadeAudioPlayer 双 WinRT MediaPlayer 实例能同时发声(平台无互斥);
    /// ③ 交叉切换后状态事件来自新实例、无错误;④ 硬切回落路径正常。听感(是否真的渐变)仍需人耳验收。</summary>
    public static async Task RunXfadeTestAsync()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var dir = Path.Combine(Path.GetTempPath(), "aly-xfade-test");
        Directory.CreateDirectory(dir);
        var fileA = WriteToneWav(Path.Combine(dir, "tone-440.wav"), 440.0, 8);
        var fileB = WriteToneWav(Path.Combine(dir, "tone-660.wav"), 660.0, 8);
        Console.WriteLine("[xfade] 已生成测试音源(440Hz/660Hz 各 8s)");

        var backend1 = new WindowsMediaPlayer(new DispatcherService());
        var backend2 = new WindowsMediaPlayer(new DispatcherService());
        var wrapper = new CrossfadeAudioPlayer(backend1, () => backend2);
        var errors = new List<string>();
        wrapper.ErrorOccurred += (_, e) => errors.Add(e);

        wrapper.Volume = 55;
        wrapper.PlayUrl(fileA);
        var started = false;
        for (var i = 0; i < 100 && !started; i++)
        {
            await Task.Delay(100);
            started = wrapper.State == PlaybackState.Playing;
        }

        Console.WriteLine(started
            ? $"[xfade] A 段起播 OK(wrapper.State=Playing, dur={wrapper.DurationMs}ms)"
            : "[xfade] FAIL A 段 10s 内未进入 Playing");
        if (!started)
        {
            Environment.ExitCode = 1;
            return;
        }

        // 交叉切到 B 段:A 渐弱 B 渐强,总时长 1.5s
        var swapped = await wrapper.TryCrossfadePlayAsync(fileB, 1500);
        Console.WriteLine(swapped ? "[xfade] 交叉切换被接受" : "[xfade] FAIL 交叉切换被拒");
        await Task.Delay(2500);

        var stillPlaying = wrapper.State == PlaybackState.Playing;
        Console.WriteLine(stillPlaying
            ? $"[xfade] 切换后仍 Playing(pos={wrapper.PositionMs}ms,dur={wrapper.DurationMs}ms)"
            : "[xfade] FAIL 切换后掉出 Playing");
        Console.WriteLine(errors.Count == 0
            ? "[xfade] 无错误事件"
            : $"[xfade] FAIL 错误事件: {string.Join("; ", errors)}");

        // 硬切回落:非交叉状态下 PlayUrl 直接换源
        wrapper.PlayUrl(fileA);
        await Task.Delay(500);
        var hardOk = wrapper.State == PlaybackState.Playing;
        Console.WriteLine(hardOk ? "[xfade] 硬切回落 OK" : "[xfade] FAIL 硬切后未播放");

        wrapper.Stop();
        Environment.ExitCode = swapped && stillPlaying && errors.Count == 0 && hardOk ? 0 : 1;
    }

    /// <summary>写一段 16bit 单声道 44.1kHz 正弦波 WAV(两端 10ms 淡入淡出防爆音)。</summary>
    private static string WriteToneWav(string path, double freq, double seconds)
    {
        const int sampleRate = 44100;
        var total = (int)(sampleRate * seconds);
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms))
        {
            w.Write("RIFF"u8);
            w.Write(36 + total * 2);
            w.Write("WAVE"u8);
            w.Write("fmt "u8);
            w.Write(16);
            w.Write((short)1); // PCM
            w.Write((short)1); // mono
            w.Write(sampleRate);
            w.Write(sampleRate * 2);
            w.Write((short)2);
            w.Write((short)16);
            w.Write("data"u8);
            w.Write(total * 2);
            for (var i = 0; i < total; i++)
            {
                var t = i / (double)sampleRate;
                var fade = Math.Min(1.0, Math.Min(i / 441.0, (total - i) / 441.0)); // ±10ms
                w.Write((short)(Math.Sin(2 * Math.PI * freq * t) * 0.6 * fade * short.MaxValue));
            }
        }

        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    public static async Task RunAsync()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var services = new ServiceCollection();
        services.AddSingleton<CookieStore>();
        services.AddSingleton<CnIpPool>();
        services.AddSingleton<CryptoService>();
        services.AddSingleton<NetEaseApiClient>();
        services.AddSingleton<QQMusicApiClient>();
        services.AddSingleton<IMusicApi>(sp => sp.GetRequiredService<NetEaseApiClient>());
        services.AddSingleton<IMusicApi>(sp => sp.GetRequiredService<QQMusicApiClient>());
        services.AddSingleton<MusicApiProvider>();
        await using var sp = services.BuildServiceProvider();

        var crypto = sp.GetRequiredService<CryptoService>();
        var api = sp.GetRequiredService<NetEaseApiClient>();

        try
        {
            // ---- 固定 key 交叉验证输出(与 Python 参考对比)----
            var fixedResult = crypto.EncryptWeapiWithKey("{\"s\":\"晴天\",\"type\":1}", "abcdefghijklmnop");
            Console.WriteLine($"[fixed] params={fixedResult.Params}");
            Console.WriteLine($"[fixed] encSecKey={fixedResult.EncSecKey}");

            Console.WriteLine("[lrc] 单元自检:多标签/排序/offset/翻译合并...");
            var sampleOrig = "[ar:测试]\n[offset:500]\n[00:05.00]第二句\n[00:01.00]第一句\n[00:05.00]第二句重放\n";
            var sampleTrans = "[00:01.02]翻译一\n[00:05.02]翻译二\n";
            var sd = Services.Lrc.LrcParser.Parse(sampleOrig, sampleTrans);
            var lrcOk = sd.Lines.Count == 3
                        && sd.Lines[0].TimeMs == 1000 && sd.Lines[0].Translation == "翻译一"
                        && sd.Lines[1].TimeMs == 5000 && sd.Lines[1].Translation == "翻译二"
                        && sd.Lines[2].TimeMs == 5000 && sd.Lines[2].HasTranslation
                        && sd.Offset.TotalMilliseconds == 500
                        && sd.FindIndex(400) == -1
                        && sd.FindIndex(500) == 0
                        && sd.FindIndex(60_000) == 2;
            Console.WriteLine(lrcOk ? "[lrc] 单元自检 OK" : $"[lrc] 单元自检 FAIL(行数 {sd.Lines.Count})");

            Console.WriteLine("[anon] 匿名注册中(weapi 通道)...");
            try
            {
                await api.EnsureAnonymousAsync();
                Console.WriteLine("[anon] OK,加密通道可用");
            }
            catch (Services.NetEase.ApiException ex)
            {
                Console.WriteLine($"[anon] 匿名注册失败({ex.Message})→ 走明文接口回落");
            }

            Console.WriteLine("[search] 搜索: 晴天 周杰伦");
            var songs = await api.SearchAsync("晴天 周杰伦", 5);
            Console.WriteLine($"[search] {songs.Count} 条结果");
            foreach (var s in songs.Take(3))
                Console.WriteLine($"  {s.Id}  {s.Name} - {s.Artist}  {s.DurationMs}ms");

            if (songs.Count > 0)
            {
                var id = songs[0].Id;
                Console.WriteLine($"[playurl] id={id}");
                var url = await api.GetPlayUrlAsync(id, "standard");
                Console.WriteLine(url?.Url is null
                    ? "[playurl] 结果: URL 为 null(免费不可播/VIP/风控)"
                    : $"[playurl] OK: {url.Url[..Math.Min(70, url.Url.Length)]}... isTrial={url.IsTrial}");

                Console.WriteLine("[lyric] 遍历前几条结果,找第一条有时间轴歌词...");
                var found = false;
                foreach (var s in songs.Take(5))
                {
                    var lrc = await api.GetLyricAsync(s.Id);
                    if (lrc is null) continue;
                    var doc = Services.Lrc.LrcParser.Parse(lrc.Original, lrc.Translation);
                    if (doc.Lines.Count == 0) continue; // 无时间轴(如翻唱曲的纯文本)
                    var withTrans = doc.Lines.Count(l => l.HasTranslation);
                    Console.WriteLine($"[lyric] {s.Id} {s.Name} 解析 {doc.Lines.Count} 行,带翻译 {withTrans} 行");
                    Console.WriteLine($"[lyric] 首句 [{doc.Lines[0].TimeMs}ms] {doc.Lines[0].Original}");
                    var idx = doc.FindIndex(60_000);
                    Console.WriteLine($"[lyric] 二分 @60s → 第 {idx + 1} 行: {doc.Lines[idx].Original}");
                    found = true;
                    break;
                }
                if (!found)
                    Console.WriteLine("[lyric] 前 5 首都无时间轴歌词(空态演示)");

            }
            else
            {
                Console.WriteLine("[playurl][lyric] 跳过(无搜索结果)");
            }

            // ---- M4 登录/歌单 ----
            Console.WriteLine("[playlist] 公开歌单曲目(飙升榜 19723756,无需登录)...");
            var plSongs = await api.GetPlaylistDetailAsync(19723756);
            Console.WriteLine($"[playlist] 歌单曲目 {plSongs.Count} 首, 首曲 {plSongs[0].Name} - {plSongs[0].Artist}");

            Console.WriteLine("[profile] 未登录调用户资料(应报未登录)...");
            try
            {
                await api.GetUserProfileAsync();
                Console.WriteLine("[profile] 意外成功(?)");
            }
            catch (ApiException ex)
            {
                Console.WriteLine($"[profile] 符合预期: {ex.Message}");
            }

            // ---- 首页推荐(明文 GET) ----
            Console.WriteLine("[recommend] 首页推荐区块(每日/推荐歌单/热门/猜你喜欢)...");
            var rPl = await api.GetPersonalizedPlaylistsAsync(3);
            Console.WriteLine($"[recommend] 推荐歌单 {rPl.Count} 个, 首 [{rPl.FirstOrDefault()?.Title}] [{rPl.FirstOrDefault()?.Subtitle}] cover={rPl.FirstOrDefault()?.CoverUrl}");
            var rHot = await api.GetPlaylistDetailAsync(3778678);
            Console.WriteLine($"[recommend] 热歌榜 {rHot.Count} 首, 首 [{rHot.FirstOrDefault()?.Name} - {rHot.FirstOrDefault()?.Artist}] cover={rHot.FirstOrDefault()?.CoverUrl}");
            var rNew = await api.GetNewSongsAsync(3);
            Console.WriteLine($"[recommend] 猜你喜欢 {rNew.Count} 个, 首 [{rNew.FirstOrDefault()?.Title}] [{rNew.FirstOrDefault()?.Subtitle}] cover={rNew.FirstOrDefault()?.CoverUrl}");
            var rDaily = await api.GetDailyRecommendAsync();
            Console.WriteLine($"[recommend] 每日推荐(未登录应空) {rDaily.Count} 个");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FAIL] {ex.GetType().Name}: {ex.Message}");
            Environment.ExitCode = 1;
        }
    }
}
