#if ANDROID
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ALyricEase.Infrastructure;

namespace ALyricEase.Services.Audio;

/// <summary>Android 原生音频播放(global::Android.Media.MediaPlayer)。
/// 首选直接流播网络 URL(原生渐进式边下边播):http:// URL 换成 https:// 规避
/// native 栈明文限制;流播失败时兜底用 HttpClient 下载到缓存文件再播本地文件。
/// 通过轮询向 UI 线程上报进度/时长/状态;支持播放、暂停、继续、Seek、音量。</summary>
public sealed class AndroidMediaPlayer : IAudioPlayer
{
  private const string Tag = "ALyricEasePlayer";
  private const int PollIntervalMs = 200;
  private const string UserAgent =
      "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36";

  private readonly DispatcherService _dispatcher;
  private readonly global::Android.Media.MediaPlayer _mp;
  private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
  private readonly Timer? _timer;
  private readonly object _lock = new();

  private PlaybackState _state = PlaybackState.Idle;
  private bool _prepared;
  private long _lastPosMs = -1;
  private long _lastDurMs;
  private string? _cachePath;
  private string? _currentUrl;

  // 代数号:每次 PlayUrl/Stop 自增,迟到的错误回调/下载完成回调据此丢弃,防竞态。
  private int _generation;
  // 当前处于哪种播放模式:流播失败自动降级为下载,下载模式再失败才报错。
  private bool _downloading;

  public AndroidMediaPlayer(DispatcherService dispatcher)
  {
    _dispatcher = dispatcher;
    _mp = new global::Android.Media.MediaPlayer();
    _timer = new Timer(_ => Poll(), null, PollIntervalMs, PollIntervalMs);
    _mp.Prepared += OnPrepared;
    _mp.Completion += (_, _) => _dispatcher.Post(() => RaiseState(PlaybackState.Idle));
    _mp.Error += OnError;
  }

  private void OnPrepared(object? sender, EventArgs e)
  {
    // 直接在回调线程 Start,避免经 UI 线程转发被竞态吞掉;状态上报仍走 UI 线程。
    try
    {
      _prepared = true;
      _mp.Start();
      Log("Prepared, started streaming");
    }
    catch (Exception ex)
    {
      Log($"Start after prepare failed: {ex.Message}");
    }
    _dispatcher.Post(() => RaiseState(PlaybackState.Playing));
  }

  private void OnError(object? sender, global::Android.Media.MediaPlayer.ErrorEventArgs e)
  {
    var what = (int?)e?.What ?? -1;
    var extra = (int?)e?.Extra ?? -1;
    Log($"MediaPlayer error what={what} extra={extra} mode={( _downloading ? "download" : "stream" )}");
    int gen;
    string? url;
    lock (_lock)
    {
      gen = _generation;
      url = _currentUrl;
    }
    if (!_downloading && url is { Length: > 0 })
    {
      // 流播失败,降级为下载后播本地文件
      Log("Falling back to download mode");
      _dispatcher.Post(() =>
      {
        if (gen != Volatile.Read(ref _generation)) return;
        ResetPlayerInternal();
        RaiseState(PlaybackState.Loading);
        _ = DownloadAndPlayAsync(url, gen);
      });
      return;
    }
    _dispatcher.Post(() =>
    {
      if (gen != Volatile.Read(ref _generation)) return;
      ErrorOccurred?.Invoke(this, $"播放出错({what}/{extra})");
      ResetPlayerInternal();
      RaiseState(PlaybackState.Idle);
    });
  }

  public PlaybackState State => _state;

  public long PositionMs
  {
    get
    {
      try
      {
        return _prepared ? _mp.CurrentPosition : 0;
      }
      catch
      {
        return 0;
      }
    }
    set
    {
      try
      {
        if (_prepared) _mp.SeekTo((int)Math.Max(0, value));
      }
      catch
      {
      }
    }
  }

  public long DurationMs
  {
    get
    {
      try
      {
        return _prepared ? _mp.Duration : 0;
      }
      catch
      {
        return 0;
      }
    }
  }

  private int _volume;

  public int Volume
  {
    // Android MediaPlayer 无 getVolume(),用私有字段回读(避免编译不过)
    get => _volume;
    set
    {
      _volume = value;
      var v = Math.Clamp(value, 0, 100) / 100f;
      _mp.SetVolume(v, v);
    }
  }

  public event EventHandler? StateChanged;

  public event EventHandler<long>? PositionChanged;

  public event EventHandler<long>? DurationChanged;

  public event EventHandler<string>? ErrorOccurred;

  public void PlayUrl(string url)
  {
    Log($"PlayUrl: {url}");
    StopInternal();

    // http 明文 URL 换 https,规避 native 网络栈不遵循 usesCleartextTraffic 的问题
    var streamUrl = url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        ? "https://" + url["http://".Length..]
        : url;

    lock (_lock)
    {
      _generation++;
      _currentUrl = streamUrl;
      _downloading = false;
    }

    RaiseState(PlaybackState.Loading);
    try
    {
      _mp.Reset();
      var headers = new Dictionary<string, string>
      {
        ["User-Agent"] = UserAgent,
        ["Referer"] = "https://music.163.com",
      };
      var context = global::Android.App.Application.Context;
      _mp.SetDataSource(context, global::Android.Net.Uri.Parse(streamUrl)!, headers);
      Log("SetDataSource(stream) OK");
      _mp.PrepareAsync();
    }
    catch (Exception ex)
    {
      Log($"Stream SetDataSource exception: {ex.Message}");
      // native 层直接拒绝(如 cleartext),走下载兜底
      _ = DownloadAndPlayAsync(streamUrl, Volatile.Read(ref _generation));
    }
  }

  /// <summary>兜底路径:把音频下载到缓存文件再播本地文件。</summary>
  private async Task DownloadAndPlayAsync(string url, int gen)
  {
    try
    {
      Log("Downloading...");
      lock (_lock) _downloading = true;
      var dir = global::Android.App.Application.Context.CacheDir!.AbsolutePath;
      var target = Path.Combine(dir, $"playback_{Guid.NewGuid():N}.mp3");
      using (var req = new HttpRequestMessage(HttpMethod.Get, url))
      {
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        req.Headers.TryAddWithoutValidation("Referer", "https://music.163.com");
        using var resp = await _http.SendAsync(req).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
          Log($"Download failed status={(int)resp.StatusCode}");
          _dispatcher.Post(() =>
          {
            if (gen != Volatile.Read(ref _generation)) return;
            ErrorOccurred?.Invoke(this, $"播放出错(HTTP {(int)resp.StatusCode})");
            RaiseState(PlaybackState.Idle);
          });
          return;
        }

        await using (var fs = File.Create(target))
          await resp.Content.CopyToAsync(fs).ConfigureAwait(false);
      }

      Log($"Downloaded to {target}");
      if (gen != Volatile.Read(ref _generation))
      {
        TryDelete(target);
        return;
      }

      _dispatcher.Post(() =>
      {
        if (gen != Volatile.Read(ref _generation))
        {
          TryDelete(target);
          return;
        }
        try
        {
          _cachePath = target;
          _mp.Reset();
          _mp.SetDataSource(target); // 本地文件,无 cleartext 限制
          Log("SetDataSource(local) OK");
          _mp.PrepareAsync();
        }
        catch (Exception ex)
        {
          Log($"Local play exception: {ex.Message}");
          ErrorOccurred?.Invoke(this, $"播放出错({ex.Message})");
          RaiseState(PlaybackState.Idle);
        }
      });
    }
    catch (Exception ex)
    {
      Log($"Download exception: {ex.Message}");
      _dispatcher.Post(() =>
      {
        if (gen != Volatile.Read(ref _generation)) return;
        ErrorOccurred?.Invoke(this, $"播放出错({ex.Message})");
        RaiseState(PlaybackState.Idle);
      });
    }
  }

  public void Pause()
  {
    try
    {
      _mp.Pause();
    }
    catch
    {
    }
  }

  public void Resume()
  {
    try
    {
      _mp.Start();
    }
    catch
    {
    }
  }

  public void Stop()
  {
    StopInternal();
    RaiseState(PlaybackState.Idle);
  }

  private void StopInternal()
  {
    lock (_lock)
    {
      _generation++;
      _currentUrl = null;
      _downloading = false;
    }
    ResetPlayerInternal();
    DeleteCacheFile();
    _lastPosMs = -1;
    _lastDurMs = 0;
  }

  /// <summary>把播放器复位到干净状态(_prepared 复位、停止 native 播放),不动代数号。</summary>
  private void ResetPlayerInternal()
  {
    _prepared = false;
    try
    {
      _mp.Stop();
    }
    catch
    {
    }
    try
    {
      _mp.Reset();
    }
    catch
    {
    }
  }

  public void Dispose()
  {
    StopInternal();
    try
    {
      _mp.Release();
    }
    catch
    {
    }
    _timer?.Dispose();
    DeleteCacheFile();
  }

  private void DeleteCacheFile()
  {
    if (_cachePath is { } path) TryDelete(path);
    _cachePath = null;
  }

  private static void TryDelete(string path)
  {
    try
    {
      if (File.Exists(path)) File.Delete(path);
    }
    catch
    {
    }
  }

  private void Poll()
  {
    try
    {
      if (!_prepared) return;
      var pos = PositionMs;
      var dur = DurationMs;
      if (pos != _lastPosMs)
      {
        _lastPosMs = pos;
        var p = pos;
        _dispatcher.Post(() => PositionChanged?.Invoke(this, p));
      }

      if (dur != _lastDurMs)
      {
        _lastDurMs = dur;
        var d = dur;
        _dispatcher.Post(() => DurationChanged?.Invoke(this, d));
      }

      if (_mp.IsPlaying && _state != PlaybackState.Playing)
        _dispatcher.Post(() => RaiseState(PlaybackState.Playing));
      else if (!_mp.IsPlaying && _prepared && _state == PlaybackState.Playing)
        _dispatcher.Post(() => RaiseState(PlaybackState.Paused));
    }
    catch
    {
    }
  }

  private void RaiseState(PlaybackState s)
  {
    if (s == _state) return;
    _state = s;
    StateChanged?.Invoke(this, EventArgs.Empty);
  }

  private static void Log(string message) => global::Android.Util.Log.Debug(Tag, message);
}
#endif
