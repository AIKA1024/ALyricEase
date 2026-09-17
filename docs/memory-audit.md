# 内存方案总览与审计

审计日期：2026-09-16。范围：`src/ALyricEase`（Windows / Android / Headless 三宿主共用同一套核心库）。
本文件只记录**当前实现**与**已核实的疑点**，未核实的标注出来，不做推测性结论。

## 〇、处置状态

第三章的五项已全部实施完毕，对应条目标了 ✅，正文里描述"修复前"的表格与段落也已就地更新为当前实现：

| 条目 | 处置方式 |
|---|---|
| P1-1 图片预算不分平台 | 新增 `Infrastructure/ImageMemoryBudget.cs` 作为唯一来源，运行时按平台取值（真机生效值已核对，见下） |
| P1-2 `_pendingSnapshotWrites` 只增不减 | 随磁盘写入层整体移除 |
| P1-3 换号红心串味 | 各音源客户端引入**账号代次**（`AccountGeneration`），换号/登出即作废红心缓存与在途请求 |
| P2-6 磁盘快照层 | 整体删除，快照只驻留内存；容量 8 → 32 页 |
| P2-7 磁盘读抢 `_mutationGate` | 第一轮随磁盘快照路径消失；第二轮把**其余四个读路径**也摘出来（见 P2-7 正文，有实测） |
| P1-4 写盘路径的全目录扫描（2026-09-16 追加） | `EnsureSpaceFor` 加 `_knownCacheBytes` 工作副本快路径，封面落盘 551.5ms → 1.8ms/张（见 §5） |
| P1-5 不确定进度条的最小化动画（2026-09-16 追加） | 新增 `Infrastructure/IndeterminateAnimationGate` 附加属性，最小化时停掉框架主题动画（同轮 A/B 净代价 2.60% → 0.00%，见 `docs/avalonia-tips.md`） |
| P1-6 详情页色团漂移的 GPU（2026-09-17 追加） | `AlbumCoverBackground` 加 `MotionEnabled`，宿主按 `ShowNowPlaying × AppState.DynamicBackground` 绑定。首页 GPU **8.16% → 0.95%**、CPU **22.5% → 1.8%**（同轮消融，漂移本身值 7.50%，图层本身只值 2.20%，见 `docs/avalonia-tips.md`「GPU 也要归因」） |

未动：P2-4（五个手写 LRU 的共用抽象）、P2-5（两套图片缓存的占用口径）、P2-8（`_offlineIndex` 失配）与 P3 全部。

**真机核对（2026-09-16）**：五项处置在真机上跑通。新增 Android 真机自检入口
`src/ALyricEase.Android/Diagnostics/DeviceImageBudgetProbe.cs` —— 由 intent extra 触发、只读、输出到 logcat：

```
adb shell am start -n com.aika1024.alyricease/<MainActivity> --es aly_probe image-budget
adb logcat -d -s ALyricEaseProbe:I
```

它打印预算生效值、GC/ART/ActivityManager 三方的内存事实、以及两个缓存实例**实际持有**的上限
（反射读取，用来排除"预算改了但缓存没跟着改"）。数据与结论见 P1-1 正文。
同一次真机验证还查出**只有 Release 包才会暴露的两个问题**（Debug 构建与桌面探针都发现不了）：
页面视图构造函数被裁剪导致启动即崩、Debug 包手动 adb 安装需先关 Fast Deployment —— 均记入 `docs/avalonia-tips.md`。

## 一、五层结构

### 1. 图片解码内存（两套并存）

| | `BoundedImageMemoryCache` | `CoverLoader` |
|---|---|---|
| 位置 | `Infrastructure/BoundedImageMemoryCache.cs` | `Infrastructure/CoverLoader.cs` |
| 预算 | `ImageMemoryBudget.LeaseCache*`：桌面 64MB **且** 512 项；Android **ART 堆上限 / 4** 夹在 [12MB, 48MB]，条目按 192KB/张折算（下限 48） | `ImageMemoryBudget.DirectCache*`：桌面 16MB **且** 128 项；Android `max(4MB, 租约字节/4)`、条目 `max(32, 租约条目/4)` |
| 淘汰语义 | 真 `Dispose()` 位图 | 只从字典摘引用，位图交给 GC |
| 租约 | 有（可见控件持有期间不淘汰） | 无 |
| 使用者 | XAML 里的 `Image` 控件（`ManagedCoverImage` 附加属性） | 播放器等需要直接拿 `IImage` 的低频大图 |
| 清理口 | `CoverImagePipeline.ClearMemoryCache()` | `CoverLoader.ClearMemoryCache()` |

两者共用一个磁盘字节缓存（`MusicCoverByteCache` → `MusicCacheService`），所以"同一封面"不会重复存两份**文件**。

> 预算取值集中在 `ImageMemoryBudget`（单一来源），两个缓存不再各自硬编码。
> ⚠️ 平台判定用 `OperatingSystem.IsAndroid()`：核心库只面向 `net10.0`，**`#if ANDROID` 在本程序集里是死分支**
> （ANDROID 常量只由 Android SDK 对 `net*-android` 工程定义，实测产出的 `ALyricEase.dll` 里没有任何 Android 类型名）。
> 同一陷阱适用于项目里其它写在核心库中的平台分支，相关位置已加警示注释。

### 2. 页面数据（VM 生命周期）

- **所有页面 VM 都是 Singleton**（`Program.cs:84-96`、`Android/App.axaml.cs:84-96`），进程内只有一份。
- 离页时 `ReleaseCurrentPageData()` 清空曲目、队列、分页游标等重数据；
- 同时 `CaptureAndReleaseNavigationSnapshot()` 把"这一刻的页面状态"存成快照，导航历史里只留几十字节的 record；
- 返回时 `RestoreNavigationSnapshotAsync()` 先查内存快照，命中即同步恢复（实测 1~2ms），未命中回退常规缓存/网络打开流程。

快照**只驻留内存**：`SnapshotMemoryCache<T>`（歌单/详情各 32 页，600 首 ≈ 131KB/页；合计上限约 4MB 量级）。
捕获与取回都是同步操作（`Cache*` / `TryTake*` 只做字典操作，返回 `Task.CompletedTask`/`Task.FromResult`），
所以返回恢复不需要等待任何 I/O、也不抢 `_mutationGate`。容量按"淘汰几乎不发生"配置：
历史深度 50 是 LIFO 返回，最近 32 层必然命中，容量外被淘汰的那一层退回常规加载。

历史版本写过一次性磁盘快照（`n-<session>-<hash>.snapshot`，写后读删）。该层已整体删除：
`CacheKey` 是进程内新生成的 GUID，磁盘副本只有当前进程会读，与内存副本功能等价。
升级前遗留的快照文件由 `MusicCacheService.DeleteLegacyPageSnapshotFiles()`（构造函数内）清掉。

### 3. 列表与视图

- `SongGridView.Columns` 用 `VirtualizingStackPanel Orientation="Horizontal"`（横向虚拟化）—— 这是首页/列表内存的关键，去掉会让实化行数翻 3 倍。
- `ReusablePageViewTemplate`（`Infrastructure/`）：首页视图复用。**实测只省约 1%**，因为容器生成器在重新挂树时被重置，子项整批作废。
- `RangeObservableCollection`：批量增删只发一次通知，避免逐条派发引起的重复布局。

### 4. 播放队列

`ILazySongQueue`（`Infrastructure/LazySongQueue.cs`）：

- `IndexedSongQueue`：逻辑上持全量 trackId 列表，按需解析 `Song`，解析结果进 `BoundedSongCache`（384 首）。
- `AggregateSongQueue`：成员只保计数，命中时才取该成员的 trackId 概览或单首。
- 界面已取到的 `Song` 通过 `Remember()` 回灌，避免播歌时重复请求。

### 5. 磁盘缓存

`MusicCacheService` 统一管理三类文件，共用同一个容量上限（默认 1024MB，可设 128MB~10GB）：

- `a-*` 音频文件（跨进程持久）
- `c-*` 封面字节（跨进程持久）
- `p-*` 歌单曲目（跨进程持久）

页面快照**不再是磁盘文件**（旧的 `n-*.snapshot` 层已删除，只在启动时清理遗留）。

**并发纪律（2026-09-16 定稿）**：`_mutationGate` 只服务**变更**缓存的操作（写入/替换/删除/裁剪/清空）
与 `TryAcquire`（申请播放租约，要按钉表锁定文件）；**所有读操作一律不占这把锁**。
原因是裁剪与每次落盘的 `EnsureSpaceFor` 都要 stat + 排序整个缓存目录（真实用户上万文件，单次 200ms 起），
读若排在后面就变成"卡不卡取决于当时有没有写撞上来"——即用户感受到的偶发卡顿。
读与并发删除竞争时统一退化：文件没了当 miss、索引正在被追加当"没有缓存"，由调用方回退常规加载。
回归探针 `--cache-read-gate`（裁剪占锁期间量各读路径耗时，改前 187ms → 改后 0ms）。

**写盘路径的 O(文件数) 扫描（2026-09-16 修）**：上一条把"读"摘出了锁，但**"写"自己仍然每次落盘
全目录扫描一次** —— `EnsureSpaceFor` 无条件调 `GetEvictionCandidates()`（枚举整个缓存目录、
每个文件建 `FileInfo`、按 `LastWriteTimeUtc` 排序）。真实用户缓存 13,899 文件 / 4074MB，
于是**每写一张封面都在持 `_mutationGate` 的情况下扫一遍全目录**。

`--cache-write-cost` 实测（同一份代码，只换缓存目录里的文件数）：

| 目录文件数 | 单张封面落盘中位数 | 6 路并发封面落盘 |
|---|---|---|
| 50 | 4.4ms | — |
| 13,000 | **551.5ms**（放大约 **125 倍**） | 串行化到 ~16s |

⇒ 用户报告的两个症状由此统一解释：滚动停止后仍在补图的几百毫秒/张 × 几十张 = "图片加载特别慢"，
且这段时间 CPU 一直有占用（`FileInfo` 建对象 + 排序）；并发的封面下载被这把锁串行化，越等越慢。

修法：给服务加一份**目录字节数的工作副本** `_knownCacheBytes`（-1 = 未校准），
`EnsureSpaceFor` 先走快路径 —— `known >= 0 && known + incoming <= maximum` 直接放行，不碰文件系统；
工作副本由所有变更路径按增量维护（写入/下载落盘 `+=`、删除 `-=`、**覆盖要减去旧长度**、
裁剪/清空/`GetCurrentSizeBytes` 之后重新校准）。修后 50 与 13,000 文件都是 **~1.8ms/张**、
6 路并发 ~60ms；对账校验通过（工作副本 130224KB == 实际扫描 130224KB，裁剪仍能把总量压到上限）。

端到端（`--pl-cpu-real`，40 个真实封面 URL、13,000 文件缓存目录）：封面出齐
**90,227ms → 16,596ms**，填充期 CPU 6.81% → 2.82%，最小化 CPU 0.00%。

## 二、内存预算总账

| 项 | 上限 | 硬约束 | 按平台区分 |
|---|---|---|---|
| `BoundedImageMemoryCache` | 桌面 64MB / 512 项；Android 12~48MB | 是（全部被租约持有时允许临时超限） | **是** |
| `CoverLoader` | 桌面 16MB / 128 项；Android ≥4MB | **否**（只摘引用，实际占用可超） | **是** |
| `SnapshotMemoryCache<PlaylistPageCacheData>` | 32 页 | 是 | 否 |
| `SnapshotMemoryCache<DetailPageCacheData>` | 32 页 | 是 | 否 |
| `BoundedSongCache` ×2（索引队列 / 聚合队列） | 384 首 | 是 | 否 |
| `QQMusicApiClient._midById` | 4096 | 是 | 否 |
| `AppStateStore._recentSongs` | 100 | 是 | 否 |
| `AppStateStore.SearchHistory` | **无** | — | — |
| `AppStateStore.AggregatePlaylists` | **无** | — | — |
| 磁盘总量 | 128MB~10GB | 是 | 否 |

## 三、问题清单

### P1 — 会实际伤到用户

**1. ✅ 图片内存预算硬编码 80MB，不分平台**

`BoundedImageMemoryCache`（64MB）与 `CoverLoader`（16MB）原先都用各自类型的默认值构造，
Windows 与 Android 走同一套（`Program.cs:98` / `Android/App.axaml.cs:98` 调用同一个 `CoverImagePipeline.Configure`）。
80MB 的桌面解码图预算对手机偏大 —— Android 上还要同时留住 32 页页面快照、歌词、播放缓冲与 UI 位图。

**实际做法（两轮，第二轮的结论见下面"输入选错"）**

新增 `Infrastructure/ImageMemoryBudget.cs` 作预算唯一来源。桌面维持 64MB/512 项 + 16MB/128 项；
Android 取**堆上限 / 4** 夹在 [12MB, 48MB]，条目数按 192KB/张折算并夹在 [48, 512]；
直接缓存按租约预算的 1/4（≥4MB）。两处调用点改为读该类的静态属性。
`--image-lifetime` 探针启动时打印 `ImageMemoryBudget.Describe()` 供核对。
桌面实测生效值：`lease=64MB/512items direct=16MB/128items platform=desktop`（无 `heapLimit` 后缀，符合预期）。

**⚠️ 第一轮实测暴露了输入选错（已修）**

初版取 `GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 8`。真机（Android 12）实测该值是
**4793.7MB** —— 它报的是**物理内存的 0.8 倍**，与 ART 堆无关。÷8 = 599MB **恒大于上限**，
`Clamp` 永远落到 48MB ⇒ "按堆缩放"在 Android 上退化为固定值（要落进 12~48MB 区间需要
`available < 384MB`，Android 上没有这种设备）。结果本身安全，但**低端机拿不到降额**：
`memoryClass=96MB` 的机器同样是 48MB + 12MB = 60MB 图片预算。

**修法**：分母改为 `Java.Lang.Runtime.GetRuntime().MaxMemory()`（≡ `ActivityManager.MemoryClass`，
Android 官方内存指导值，低端机 96/128MB、中端 192/256MB）。核心库不能引用 Android 类型
（见"#if ANDROID 死分支"），故：

- 核心库加注入点 `internal static void SetAndroidHeapLimit(long bytes)`（`Volatile.Write`），
  预算由 `Lazy<Budget>` 惰性解析（避免静态只读定型早于注入）；另留纯函数
  `LeaseBytesFor(heapLimitBytes)` 供推演各档位；未注入时退回固定 48MB 上限。
- Android 侧 `Application.OnCreate()` 里注入 `Runtime.MaxMemory()`，**必须早于 `base.OnCreate()`
  与首次读预算**；失败只记警告并退回固定上限。
- `Describe()` 在 Android 上追加 `heapLimit=...MB` 后缀，真机可一眼看出分母来源。

**真机实测（2026-09-16，MuMu 模拟器 / Android 12 / arm64 翻译层 / Release 包）**

```
budget  : image-budget lease=48MB/256items direct=12MB/64items platform=android heapLimit=192MB
scale   : 96MB→24MB 128MB→32MB 192MB→48MB 256MB→48MB 512MB→48MB
effective: leaseCache maxBytes=50331648 (48.0MB) maxItems=256
effective: directCache maxBytes=12582912 (12.0MB) maxItems=64
```

- `platform=android` 证实运行时判定链路通；`heapLimit=192MB` 证实分母已是 `Runtime.maxMemory()`，
  不再是物理内存量级。
- 缩放表证明**低端机现在确实会降额**（96MB→24MB、128MB→32MB），不再一律 48MB。
- 预算确实落到缓存实例上：反射读 `CoverImagePipeline._memoryCache._maximumBytes` = 50331648、
  `_maximumItems` = 256。
- 设备侧事实：`ActivityManager.MemoryClass=192MB`、`largeMemoryClass=512MB`、`Runtime.maxMemory()=192MB`、
  `MemoryInfo.TotalMem=5950MB`、`ART max=192MB`（均为 Android 12 / API 32 的正常取值）。
- 首页加载 9 张封面后租约占用 **0.7MB**（≈83KB/张，比代码里 192KB/张的估算小 —— 首页是缩略图）。

**2. ✅ `MusicCacheService._pendingSnapshotWrites` 只增不减**

key 是每次离页新生成的 GUID，落盘成功后 `ObservePageSnapshotWriteAsync` 只清 `_pageSnapshotWrites`，
**不动 `_pendingSnapshotWrites`** ⇒ "离页后不返回"的每次导航永久留下一条 `GUID + CancellationTokenSource`。

**实际做法**：整个磁盘写入路径（`_pendingSnapshotWrites` / `_pageSnapshotWrites` / `SnapshotWriteDelay` /
`ObservePageSnapshotWriteAsync` / `WriteJsonCacheFileAsync`）随 P2-6 一并删除，问题不存在了。

**3. ✅ 换号（不登出）后 `_likedIds` 不复位 → 红心状态跨账号串味**

`NetEaseApiClient.SetMusicUCookie` 原先只重置 `_vipLoaded`/`IsVip`，**没清** `_currentUserId` /
`_likedPlaylistId` / `_likedIds`（只有 `ClearCookie` 才清）。而 `_likedIds` 是懒加载且有守卫
（`if (_likedPlaylistId == 0 || _likedIds is not null) return`），`GetUserPlaylistsAsync` 会把
`_likedPlaylistId` 刷成新账号的 ⇒ **新账号的歌单 id 配上旧账号的红心 id 集合**，`IsLiked()` 返回错误结果。

**实际做法**：引入**账号代次**（不只是一个 memset 式的清空，因为还有在途请求的竞态）：

- 网易云与 QQ 客户端各持 `_accountGeneration`；`SetMusicUCookie`/`ClearCookie`（QQ 侧还含凭证刷新路径）
  统一走 `ResetLikeState()`：清 `_currentUserId`/`_likedPlaylistId`/`_likedIds`/`_likedLoading`/`_likedLoadTask` 并**自增代次**。
- `GetUserProfileAsync` / `GetUserPlaylistsAsync` / `LoadLikedIdsAsync` 拿代次守卫：请求返回时代次已变则
  **不往共享字段写回**，只把结果交给调用方 —— 换号瞬间在途的旧账号响应不会再污染新账号状态。
- `IUserMusicApi` 暴露 `AccountGeneration`；`SongItemViewModel` 记录 `_likedGeneration`，
  只在代次一致时才认为"已加载"；`PlayerViewModel.RefreshCurrentLiked()` 由
  `PlaylistViewModel.NotifyAccountChanged()`（登录成功/登出）触发，让播放器重判当前曲红心。
- 回归探针 `--accountlike`（`Headless/AccountLikeStateProbe.cs`）：反射注入旧账号状态 → 换号/登出 →
  断言状态清空且代次推进，两个音源各覆盖一遍。

### P2 — 结构与维护性

**4. 五个手写 LRU，没有共用抽象**

`BoundedImageMemoryCache`、`CoverLoader`、`BoundedSongCache`、`SnapshotMemoryCache<T>`、`QQMusicApiClient._midById` 各自实现了一遍"字典 + LinkedList"的 LRU，且**语义各不相同**（是否 Dispose、是否带租约、按字节还是按条数限容、是否线程安全）。

**5. 两套图片缓存的"内存占用"口径无法统一**

`BoundedImageMemoryCache` 淘汰时真 `Dispose` 位图，`_cachedBytes` 与真实占用基本一致；`CoverLoader.RemoveLocked` 只从字典移除，被控件继续引用的位图仍占内存。⇒ 图片内存预算里属于 `CoverLoader` 的那一份是**软预算**，它的 `_cacheBytes` 记账也失去参考价值。

**6. ✅ 磁盘快照层可以整体删除**

`CacheKey` 是进程内 GUID、文件名带 session 前缀、启动清残留 —— 磁盘副本**只有当前进程会读**，与内存副本功能等价。而内存 LRU 只有 8 页、每页 131KB（提到 32 页也才 ~4MB）。

**实际做法**：删除 `PageSnapshotSession`、`_pendingSnapshotWrites`、`_pageSnapshotWrites`、`SnapshotWriteDelay`、
`BuildPageSnapshotFileName`、`ObservePageSnapshotWriteAsync`、`WriteJsonCacheFileAsync`、
`DeleteStalePageSnapshots` 及 `ToCachedAlbum`/`ToNavigationAlbum` 映射；`Cache*`/`TryTake*`/`Discard*`
收敛为纯内存同步操作；六个快照 JSON DTO（`PlaylistPageSnapshotFile` / `DetailPageSnapshotFile` /
`CachedPageTrackFile` / `CachedAggregateLoadStateFile` / `CachedAlbumCardFile` / `CachedArtistPageRefFile`）
及其 `[JsonSerializable]` 一并删除。`MaxInMemorySnapshots` 8 → **32**（`internal const`，探针直接读）。
启动时 `DeleteLegacyPageSnapshotFiles()` 清掉升级前遗留的 `n-*.snapshot`。

**7. ✅ 读路径抢 `_mutationGate`**

`TryTake*PageSnapshotAsync` 读盘前 `await _mutationGate.WaitAsync()`，而该 gate 被下载落盘、曲目缓存写入、
`TrimToLimitAsync`（全目录 stat，实测 550ms 级）共享 ⇒ 深层返回（内存未命中）时可能等上百毫秒。

**第一轮实际做法**：随 P2-6 消失 —— 内存快照是同步字典操作，不再进入 `_mutationGate`。

**第二轮（2026-09-16 复查发现"只杀掉了一个实例"）**

第一版收尾时写的"该 gate 现在只服务于真正的缓存变更"**与代码不符**：仍留在 gate 上的还有四个读路径 ——
`GetAudioCacheAvailability`（扫 `a-*`）、`TryGetPlaylistLibrary`（纯内存字典）、
`TryGetPlaylistTracksAsync`（读 `p-*.tracks`）、`TryReadBytesAsync`（读封面/歌词字节）。
它们不需要排队，却排在写入与裁剪后面，而 gate 一旦被扫描类操作占住就是百毫秒级。

新增探针 `--cache-read-gate`（`Headless/CacheReadGateProbe.cs`）把这件事量了出来：
撑起 4000 个文件的目录，用 `SetMaximumSizeMbAsync`（=强制裁剪，整段扫描都在 gate 里）造窗口，
**每个读都配一次独立窗口**（若共用一次，第一个读会把整个等待吃掉、后面的读量到 0ms，会掩盖问题）。

| 读路径 | 空闲 | 裁剪占锁期间（改前） | 裁剪占锁期间（改后） |
|---|---|---|---|
| 封面字节（读盘） | 3ms | **187ms** | 0ms |
| 歌词字节（读盘） | 1ms | **183ms** | 0ms |
| 歌单曲目（读盘） | 3ms | **184ms** | 0ms |
| 离线歌单索引（纯内存） | 0ms | **176ms** | 0ms |
| 音频存在性（扫目录） | 2ms | **178ms** | 0ms |

（裁剪占锁窗口约 200ms；改前探针 exit 1 并报"读仍在排队"，改后 exit 0。）

**修法**：读一律不占 gate —— 与并发删除竞争时统一按"当没缓存/没读到"处理（缓存本来就允许 miss）。
另加 `TouchQuietly`：LRU 的 mtime 续期失败不再丢弃已读到的内容（旧写法把它放在同一个 try 里，
续期一抛异常就变成假 miss）。**唯一保留 gate 的"读"是 `TryAcquire`**：它要挑候选文件、续期 mtime、
按钉表锁定租约，而裁剪正是按这份钉表决定"哪些能删"，拆开会出现"挑中了→被裁掉→钉了个已消失的路径"。

改动后 gate 只剩 8 个入口：写入/替换（4）、删除（1）、裁剪（1）、清空（1）、`TryAcquire`（1）。
纪律已写进 `MusicCacheService._mutationGate` 的字段注释。

**8. `_offlineIndex` 只增不减，且与磁盘文件失配**（`Services/MusicCacheService.cs:1003-1011`）

`TrimToLimitAsync` / `EnsureSpaceFor` 只 `TryDelete` 文件，**从不回写 `_offlineIndex`**。⇒ 索引里的 `Playlists` 条目会永久累积（每次同步歌单都会 `SaveOfflineIndexLocked()` 全量序列化），且索引会声称"有缓存"而文件已删。文件侧 miss 后可容忍，但索引的长期增长与全量写盘是实打实的退化。

### P3 — 小瑕疵

**9. `AppStateStore.RecentSongs` 每次访问都分配新数组**（`Services/AppStateStore.cs:37`）

`public IReadOnlyList<Song> RecentSongs => _recentSongs.Select(...).ToArray();` —— 属性 getter 里做 LINQ + `ToArray`。绑定或代码在循环里读它就会反复分配（100 元素）。**建议**：改成维护一份投影数组，在 `_recentSongs` 变更时更新。

**10. `AppStateStore.SearchHistory` 无上限**（`:32`）—— 只增不减的字符串列表，无条数上限（最近播放有 100 的上限，这里漏了）。

**11. `PlayerViewModel.Dispose` 漏退两个订阅**（`ViewModels/PlayerViewModel.cs:1263`，订阅在 `:87-88`）

只退了 `_smtc.PlayPauseRequested` / `SeekRequested`，漏了 `NextRequested` / `PreviousRequested`。两侧都是 Singleton 且 `Dispose` 只在 SelfTest 调用，**当前不造成泄漏**，但属于隐患。

**12. ~~页面快照的 `Version != 1` 检查是死代码~~** —— 随 P2-6 的磁盘快照层一并删除。

**13. `CoverImagePipeline.Configure` 若二次调用会 Dispose 旧 pipeline**（`Infrastructure/ManagedCoverImage.cs:95`）—— 当前只在启动调一次（`Program.cs:98` / `Android/App.axaml.cs:98` / `HeadlessApp.axaml.cs:78`），风险低；但若将来支持"运行时改图片预算"，需要先把在用的租约排空。

## 四、处置顺序

1. ~~**P1-3**（换号红心串味）~~ ✅ 已做：改成账号代次机制，比原计划的三行清理更硬（连在途响应竞态一起堵住）。
2. ~~**P2-7**（读路径脱离 `_mutationGate`）~~ ✅ 已做（两轮）：第一轮随磁盘快照路径删除；
   第二轮复查发现另有四个读路径仍在排队，一并摘出并用 `--cache-read-gate` 量出前后对照（187ms → 0ms）。
3. ~~**P1-2**（`_pendingSnapshotWrites` 清理）~~ ✅ 已做：随磁盘写入层一并删除。
4. ~~**P2-6**（删磁盘快照层 + 内存容量提到 24~32）~~ ✅ 已做：容量取 32。
5. ~~**P1-1**（按平台区分图片预算）~~ ✅ 已做：桌面与真机生效值均已实测（2026-09-16，MuMu / Android 12）。
   首轮真机实测发现缩放输入选错（`TotalAvailableMemoryBytes` 报的是物理内存量级，÷8 恒大于上限
   ⇒ 退化为固定 48MB，低端机拿不到降额），**已改为 `Runtime.maxMemory()`（≡ MemoryClass）作分母**，
   Android 侧启动早期注入核心库。真机复验：`heapLimit=192MB`、缩放表 `96→24 / 128→32 / 192→48`。详见 P1-1 正文。
6. 其余 P2/P3 按需。

每步都可用现有探针回归：`--accountlike`、`--playlist-lifetime`、`--detail-lifetime`、`--image-lifetime`、`--memloop-real`、`--pl-return-real`、`--cover-cache`、`--music-cache`、`--cache-read-gate`。
