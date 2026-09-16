# 内存方案总览与审计

审计日期：2026-09-16。范围：`src/ALyricEase`（Windows / Android / Headless 三宿主共用同一套核心库）。
本文件只记录**当前实现**与**已核实的疑点**，未核实的标注出来，不做推测性结论。

## 〇、处置状态

第三章的五项已全部实施完毕，对应条目标了 ✅，正文里描述"修复前"的表格与段落也已就地更新为当前实现：

| 条目 | 处置方式 |
|---|---|
| P1-1 图片预算不分平台 | 新增 `Infrastructure/ImageMemoryBudget.cs` 作为唯一来源，运行时按平台取值 |
| P1-2 `_pendingSnapshotWrites` 只增不减 | 随磁盘写入层整体移除 |
| P1-3 换号红心串味 | 各音源客户端引入**账号代次**（`AccountGeneration`），换号/登出即作废红心缓存与在途请求 |
| P2-6 磁盘快照层 | 整体删除，快照只驻留内存；容量 8 → 32 页 |
| P2-7 磁盘读抢 `_mutationGate` | 随磁盘读路径一并消失 |

未动：P2-4（五个手写 LRU 的共用抽象）、P2-5（两套图片缓存的占用口径）、P2-8（`_offlineIndex` 失配）与 P3 全部。

## 一、五层结构

### 1. 图片解码内存（两套并存）

| | `BoundedImageMemoryCache` | `CoverLoader` |
|---|---|---|
| 位置 | `Infrastructure/BoundedImageMemoryCache.cs` | `Infrastructure/CoverLoader.cs` |
| 预算 | `ImageMemoryBudget.LeaseCache*`：桌面 64MB **且** 512 项；Android `可用堆/8` 夹在 [12MB, 48MB]，条目按 192KB/张折算（下限 48） | `ImageMemoryBudget.DirectCache*`：桌面 16MB **且** 128 项；Android `max(4MB, 租约字节/4)`、条目 `max(32, 租约条目/4)` |
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

**实际做法**：新增 `Infrastructure/ImageMemoryBudget.cs` 作预算唯一来源。桌面维持 64MB/512 项 + 16MB/128 项；
Android 取 `GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 8` 夹在 [12MB, 48MB]（可用值 ≤0 时按 256MB 估），
条目数按 192KB/张折算并夹在 [48, 512]；直接缓存按租约预算的 1/4（≥4MB）。两处调用点改为读该类的静态属性。
`--image-lifetime` 探针启动时打印 `ImageMemoryBudget.Describe()` 供核对。
桌面实测生效值：`lease=64MB/512items direct=16MB/128items platform=desktop`。

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

**7. ✅ 磁盘兜底路径仍要抢 `_mutationGate`**

`TryTake*PageSnapshotAsync` 读盘前 `await _mutationGate.WaitAsync()`，而该 gate 被下载落盘、曲目缓存写入、
`TrimToLimitAsync`（全目录 stat，实测 550ms 级）共享 ⇒ 深层返回（内存未命中）时可能等上百毫秒。

**实际做法**：随 P2-6 消失 —— 内存快照是同步字典操作，不再进入 `_mutationGate`。
该 gate 现在只服务于真正的缓存变更（写入/删除/裁剪），不再有"读私有快照也要排队"的情况。

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
2. ~~**P2-7**（磁盘读脱离 `_mutationGate`）~~ ✅ 已做：随磁盘读路径一并删除。
3. ~~**P1-2**（`_pendingSnapshotWrites` 清理）~~ ✅ 已做：随磁盘写入层一并删除。
4. ~~**P2-6**（删磁盘快照层 + 内存容量提到 24~32）~~ ✅ 已做：容量取 32。
5. ~~**P1-1**（按平台区分图片预算）~~ ✅ 已做：桌面生效值已实测，Android 侧的真机目标值仍待真机核对一次。
6. 其余 P2/P3 按需。

每步都可用现有探针回归：`--accountlike`、`--playlist-lifetime`、`--detail-lifetime`、`--image-lifetime`、`--memloop-real`、`--pl-return-real`、`--cover-cache`、`--music-cache`。
