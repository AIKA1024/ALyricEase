# 播放音质与"起播那一秒"

> 结论截至 2026-11-07。改动落点:`Services/MusicCacheService.cs`(记忆读写)、
> `ViewModels/PlayerViewModel.cs`(夹档 + 记账)、`ViewModels/MainViewModel.cs`(换号清空)。
> 回归:`--quality-ceiling`。

## 一、现象:点击播放到出声约 1 秒

用户设置的音质是 **Hi-Res**(`state.json` 的 `AudioQuality = 3`),但本地缓存的音频
全部是 `a-{hash}-03.mp3`(rank 3 = exhigh / 320k)。日志 `Playback request finished`
里能直接看到矛盾:

| 歌曲 | 本地缓存 | `ElapsedMs` |
|---|---|---|
| 432780721 | 无 | **89ms**(一次请求就拿到地址) |
| 2611744362 | 有,320k | 532 / 566 / 766 / 817 / 827 / 878 / 887 / 999ms |
| 2145324205 | 有,320k | 509 / 758 / 1374ms |

**有缓存的歌反而慢十倍** —— 缓存只有在联网问完之后才被用上。

## 二、根因:请求档高于这首歌的实际档位

1. `AudioQualityMapper.GetRequestLevel` 把 `AudioQuality=3` 映射成网易云的 `hires`(rank 5)。
2. `TryAcquire(song, 5)` 要求 `file.Rank >= 5`,而磁盘上只有 rank 3 ⇒ **每次必 miss**,必然联网。
3. `NetEaseApiClient.GetPlayUrlCoreAsync` 的降级链是
   `hires → lossless → exhigh → higher → standard`,**串行** `foreach + await`。
   网易对没有权益的档位返回的是 `code=200` + `data[0].url=null`(不是错误码),
   代码只能一档一档往下试。
4. 拿到 exhigh 后,`servedRank(3) <= offlineFallback.QualityRank(3)` 成立 ⇒
   **丢掉刚拿到的地址,改播本地那个 320k 文件**。

本机实测 RTT(到 `interface.music.163.com`):首连含 TLS 260–420ms,连接复用后 80ms/次。
三档串行 ≈ 250–600ms,加 JSON 解析与 UI 线程调度 ≈ 500–900ms,再加 MediaPlayer 首缓冲,
就是用户感知的"大概一秒"。

## 三、修法:记住"服务端最多只肯给到哪一档"

### 为什么不能从缓存文件名反推

`a-{hash}-03.mp3` 里的 `03` 只是**下限**:

| 场景 | 缓存里 | 服务端实际能给 |
|---|---|---|
| 上次设置是 HQ,只请求了 320k | 03 | 可能有无损 |
| 服务端主动降级到 320k | 03 | 只有 320k |

两种情况在磁盘上长得一模一样。把缓存当上限,用户把设置从 HQ 调到 Hi-Res 后就再也提不上去。

### 记账的唯一判据

**只有"请求更高档、返回更低档"才是上限证据**(`NoteServedQualityCeiling`):

- 请求 rank 5 → 返回 rank 3:写 `ceiling = exhigh`
- 请求 rank 3 → 返回 rank 3:什么都不写(可能还有更高档)
- 请求 rank 3 → 返回 rank 4:什么都不写(上限只会更高)

### 存储

```
%LOCALAPPDATA%\ALyricEase.Data\cache\music\q-{songKey}.probe
内容:exhigh|1794628800        # level|过期 Unix 秒(7 天)
```

- `songKey` 与音频缓存共用 `BuildSongKey`(含音源前缀,两源天然隔离)
- 放缓存目录而不是 `state.json`:`state.json` 是用户设置、会被窗口位置/音量频繁写回,
  几千条映射塞进去每次保存都要全量序列化;放缓存目录还能跟 `ClearAsync` 一起死
- 写走 `_mutationGate` 并 `AddKnownCacheBytes`(和 `StoreBytesAsync` 同一套字节账),
  **不能复用 `StoreBytesAsync`**:它对已存在的文件只 touch mtime 不覆盖内容
- 读有内存负缓存(`ConcurrentDictionary<string, QualityCeiling>`),进程内一首歌只读一次盘;
  内存条目也带过期时间,长期不重启也会自然失效

### 生命周期

| 事件 | 处理 |
|---|---|
| 7 天 TTL 到期 | 读取时失效;`TrimToLimitAsync` 顺带删文件(借已枚举的目录列表,零额外 I/O) |
| 换号 / 登出 | `MainViewModel.OnPlaylistLoginChanged` → `ClearServedQualityCeilings()` 全清 |
| 用户清缓存 | `ClearAsync` 枚举全目录,`q-*` 一并删除 |
| 服务端给了更高档 | 不写,记忆自然被 TTL 淘汰后重新探测 |

### ⚠ 裁剪**不能**按"有没有音频文件"清记忆

第一版探针就踩到了:QQ 那首歌没有音频文件,记忆写下后被下一次 `MusicCacheService`
构造时的裁剪当成孤儿直接删掉。

记忆的价值场景之一恰恰是"**歌没缓存过、但已知上限**"(用户只在线播、或缓存被清过),
按音频存在与否删会让这种记忆写下就消失。判据只能是 TTL —— 每条 72 字节,
十万首也才 7MB,增长由 TTL 兜住。

## 四、效果

| 路径 | 取址次数 | 起播 |
|---|---|---|
| 修前(缓存档 < 设置档) | 3 次串行 | 500–900ms |
| 修后(有记忆 + 有 320k 缓存) | **0 次** | 本地文件直读 |
| 修后(有记忆、无缓存) | 1 次 | ~120ms |
| 修后(首次探测,仍要降级) | 3 次 | 500–900ms,但**只此一次** |

日志:`Playback quality ceiling learned. Source=... SongId=... Requested=hires Served=exhigh`
—— 自动降档必须可见,别让用户以为设置没生效。

## 五、已知边界

- **改高设置不会立刻提档**:记忆说上限 3,用户把设置调到 Hi-Res 后仍然请求 3,
  要等 7 天 TTL 到期才重新探测。这是刻意的 —— 上次确实被拒过。
  需要立即重试时删掉对应的 `q-*.probe` 即可。
- **数字专辑/单曲购买不受影响**:`fee=4` + `IsPurchased` 是独立的可播性判定,
  记忆只夹音质档位,不碰能不能播。
- **下载不走这条链**:`TryAcquireForDownload` 是用户显式选档,必须精确匹配,不夹。
- **母带/空间音频未接**:`AudioQuality` 只有 4 档,最高到 `hires`(rank 5);
  rank 6 的 `jymaster` / `sky` / QQ 臻品档目前没有入口。

## 六、探针

```powershell
dotnet run --project src/ALyricEase.Headless -c Debug -- --quality-ceiling
```

覆盖:无记忆透传、降级落盘、夹档、**夹档后零取址直接命中缓存**(核心收益)、
两条反例(同档/返回更高档都不写)、QQ 字母档位、TTL 过期、换号清空、
"无音频但未过期要保留"与"过期要删掉"。
