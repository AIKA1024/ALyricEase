# 关注歌手新歌（浏览页「关注新歌」区块）

> 2026-11 实现。数据源结论全部是**本机登录态实测**，不是文档推断。

## 1. 两个平台都没有"关注歌手新歌"的聚合接口

| 尝试 | 结果 |
|---|---|
| 网易云 `GET/POST /api/artist/new/song` | **404**（NeteaseCloudMusicApi 里那个 `artist_new_song` 在明文通道不存在） |
| 网易云全站"新歌速递" `GET /api/v1/discovery/new/songs` | 能通，但 100 首里命中关注歌手 **0 首**（覆盖面太窄，不能拿来过滤） |
| QQ 客户端 `QQMusic_Protocol.dll` | 只有 `music.concern.ConcernSystem.cgi_get_all_concern_list`（关注，用户+歌手混排），**没有新歌模块** |
| QQ 网页 JS（`common.chunk`） | `music.concern.RelationList` 只有 `GetFansList` / `GetFollowUserList` / `GetFollowSingerList`，关注歌手条目**没有新歌字段** |
| QQ 全站 `QQMusic.MusichallServer/GetNewSong` | 能通，但数据停在 **2019 年**（`type=0` 还返回空），不可用 |

⇒ 只能自己拼：**关注列表 + 每位歌手按发行时间取最新**。

## 2. 两源的具体参数

### 网易云

```text
GET /api/artist/sublist?limit=200&offset=0&total=true   # 关注歌手(17 位)
GET /api/v1/artist/songs?id={artistId}&limit=4&offset=0&order=time
```

- ⚠ `artist/sublist` **必须带 `total=true`**：漏掉时即使登录态有效也回 `301`（会被误判成 cookie 失效）。
- 发行时间在 `album.publishTime`（毫秒）。`MapLegacySong` 原来**没有映射这个字段**（歌手页用不到），
  实现时补上了；缺日期的条目在窗口判断里会被丢弃，所以这个映射是功能前提。

### QQ 音乐

```text
music.concern.RelationList / GetFollowSingerList   { From, Size, HostUin:"" }   # 32 位
musichall.song_list_server / GetSingerSongList     { singerMid, order:2, number:4, begin:0 }
```

- 条目里的 `MID` 直接就是 `singerMid`（映射到 `UserRelationUser.SingerMid`）。
- **`order=2` 才是按发行时间倒序**，`order=1` 是热度（项目歌手页一直用 1，别混用）。
  实测 ONE OK ROCK `order=2` 首条 2026-10-30《Fever》，`order=1` 首条是 2011 年的《Re:make》。
- 发行时间 `album.time_public`（`yyyy-MM-dd`），`MapSongEntries` 已映射到 `Song.PublishDate`。

## 3. 服务层策略（`Services/FollowedNewSongService.cs`）

近 50 次请求不能每次进页面都跑，所以：

| 机制 | 值 | 原因 |
|---|---|---|
| 时间窗口 | 最近 **3 个月** | 产品口径；窗口外条目在合并前丢弃 |
| 每位歌手取几首 | 4 | 同一张专辑常有多首单曲，4 首足够覆盖窗口 |
| 并发 | 6 | 50 次串行要十几秒；6 并发实测 **1.7s** |
| 缓存 | 30 分钟（整个结果对象） | 刷新按钮 `force=true` 绕过 |
| 单飞 | 同一时刻只跑一份 | 页面重建/快速刷新不会叠请求 |
| **同专辑聚合** | 同源同专辑 → 一张卡 | 一张专辑连发十几首会把格子全占满（2026-11 实测 24 格几乎全被少数几张专辑吃掉） |
| **专辑全量曲目** | 每张专辑卡再拉 1 次（QQ `GetAlbumSongList` / 网易云 `/api/v1/album/{id}`） | 卡片要写专辑真实曲数、▶ 要播整张；只补截断后仍在货架上的那 16 张，6 并发实测 +0.3s |
| 条数上限 | 24 **张卡**（不是 24 首） | 货架再多也没人滚到底，还拖慢封面解码 |
| 去重 | `(Source, Id)` 再 `(Source, Name, Artist, PublishDate)` | 合唱两边各一次；QQ 同一专辑偶尔下发多条同名 |

### 同专辑聚合的键

`(源, AlbumId)` → QQ 的 `albumId` 偶为 0，退回 `(源, AlbumMid)` → 都缺时才用
`(源, 专辑名, 歌手, 发行日期)`（「精选」这类同名专辑不少，光靠名字会误并）。

聚合发生在 **截断之前**，否则一张连发十几首的专辑会先把格子占满再把它们并成一张，
结果货架上只剩一两张卡。输入已按发行日期倒序，所以组的先后天然是"组内最新那首"的先后，
组内也保持倒序，`FollowedNewSongItem.Lead` 就是这张专辑最新的一首。

截断按两类**各算上限**（单曲 24 张 / 专辑 24 张），合回一个按日期倒序的清单，界面再按
`IsAlbum` 分流。共用一个 24 会出现"单曲多的日子把专辑整块挤掉"。

`FollowedNewSongResult.Songs` 是聚合后展平的歌曲（可能远多于 24 首 —— 一张专辑卡里可能几十首），
按发行日期倒序；界面只用它分流前的 `Items`，两个货架各自按需建队列。

### 专辑卡为什么还要拉全量曲目

采样只有"每位歌手 4 首"，一张 10 首的新专辑可能只命中 4 首。如果把采样数当专辑曲数写上去，
用户会当场质疑（明透《BELOVED》专辑 10 首、卡片写 4 首）。所以聚合截断后对每张专辑卡补一次
全量曲目：

- QQ：`music.musichallAlbum.AlbumSongList/GetAlbumSongList { albumMid, num:200 }`
- 网易云：`GET /api/v1/album/{id}`（`size` 是曲数，`songs` 是全量曲目）

`FollowedNewSongItem.FullSongs` 存全量，`TotalCount` = 全量长度（拿不到时退回采样数），
`PlaybackSongs` = 全量优先、否则采样。拉取失败只影响那一张（卡片回落「N 首新歌」，▶ 播采样），
不阻塞整块。

任一侧失败只影响自己（`NetEaseFailed` / `QqFailed` 分开记），两侧都失败时不写缓存 —— 下次进页面还要重试。

## 4. UI

- 位置：浏览页**第三、四块**——「关注新歌」（单曲）与「关注新专」（专辑）相邻，都排在
  「新歌速递」**之前**（关注的人比全站榜单更重要）；逐位歌手查、明显更慢，所以独立异步，不挡前三个货架。
- 独立异步加载：前三块先上屏，它们自己在后台补（首屏不被 50 次请求拖住）。
  「关注新歌」加载期间显示 16px 转圈 + 「正在汇总关注歌手的新歌…」；「关注新专」不占位，
  有专辑才出现（空货架整块隐藏）。
- 标题右侧说明：`最近 3 个月 · 24 首`（首 = 单曲卡数）、`最近 3 个月 · 16 张专辑`；
  「关注新歌」没有命中时写「这段时间没发新单曲」（和"一个歌手都没关注"分开）。
- 卡片复用 `BrowseCardViewModel` + `browse-card` 样式（Fluent 2：8px 圆角、层叠表面、hover 遮罩），
  但**右上角改为发行时间徽章**（`release-badge`，「昨天 / 3 天前 / 2 周前」）——新歌没有播放量，那个位置空着不如给时间。
- **两条货架**（2026-11 拆分）：
  - 关注新歌：标题 = 歌名，副标题 = 完整歌手串，**点击即播放**（整条单曲队列）。
  - 关注新专：标题 = 专辑名，副标题 = `第一位歌手 · 共 N 首`（N = **专辑真实曲数**），
    封面/日期取组内最新那首，**点击进专辑页**；副标题只放第一位歌手——QQ 的合唱署名是
    「甲/乙/丙」一大串，整串铺上去会把曲数挤出单行省略号。全量曲目没拉到时才写「N 首新歌」
    （明确是采样，不冒充专辑曲数）。
- **两类卡都有悬停 ▶ / •••**（和「推荐歌单」同一套 `card-op` 圆钮、同一份位置断言），
  桌面右键 / 触屏长按走同一个 `ContextRequested` 入口：
  - ▶：单曲播这一首（接整条单曲队列）；专辑播**整张专辑**（`PlaybackSongs`，队列就是它自己，
    来源名 = 专辑名；没拉到全量时退回采样曲目）。
  - •••/右键：单曲弹歌曲菜单（加歌单/下载/表演者/专辑/分享…）、歌单卡弹歌单菜单；
    专辑卡弹**专辑菜单**，结构对齐歌单菜单（`AlbumCardContextMenu`）：
    专辑名(灰) / 播放（N 首）/ 打开专辑页 / 表演者：XXX（多歌手为子菜单）/ ─ / 分享 / 复制链接。
    缺公开链接（缺 mid/id）时整段分享项省略。分享链接见 `Services/AlbumShareLinks.cs`
    （网易云 `/album?id=`、QQ `/ryqq/albumDetail/{albumMid}`）。
- **「新歌速递」与「关注新歌」同款**（2026-11）：卡上也带 `Song` + `PlayAction`，
  悬停浮现 ▶ / •••、•••/右键弹歌曲菜单；以前是纯展示卡（只有整卡点击播放）。
- `BrowseCardViewModel` 为此新增 `Song` / `AlbumSongs` / `PlayAction`：`HasCardOps` 决定两颗圆钮显隐，
  点击行为仍由 `activate` 决定（单曲=播放，专辑=进专辑页）。

## 5. 回归

```powershell
# 数据链路(联网只读):歌手数 / 命中数 / 3 个月窗口 / 倒序 / 耗时
dotnet run --project src/ALyricEase.Headless/ALyricEase.Headless.csproj -c Debug -- --followed-new-songs

# 界面:真数据渲染 + 两条货架 + 悬停按钮 + 1120/620 无横向溢出,截图落临时目录
dotnet run --project src/ALyricEase.Headless/ALyricEase.Headless.csproj -c Debug -- --browse-followed

# 卡片手势(离线):三类卡都有 ▶/•••、三类菜单各自的结构、卡片点击不被圆钮吃掉
dotnet run --project src/ALyricEase.Headless/ALyricEase.Headless.csproj -c Debug -- --browse-ops
```

本机实测输出（2026-11-12，拆成两条货架之后）：

```
[followed-new] 歌手 网易云=17 QQ=32 失败(网易云=False QQ=False)
[followed-new] 合并 158 首(网易云 20 / QQ 138) 耗时 1.1s(cutoff 2026-08-12)
[followed-new] 货架 40 张卡(专辑卡 16 张 / 单曲卡 24 张) 队列 158 首
[followed-new]   专辑卡《BELOVED》采样 4 首 / 全量 10 首 — 明透
[followed-new]   专辑卡《崩坏星穹铁道-神说要有笑（下篇）…》采样 30 首 / 全量 48 首 — HOYO-MiX
[followed-new] PASS 专辑卡都拿到了整张曲目(曲数=专辑真实曲数)
[followed-new] PASS 全量曲目不少于采样
[followed-new] PASS 专辑卡曲数与接口 totalNum 一致
[followed-new] PASS 两类卡片各自在上限内
[followed-new] PASS 同一专辑只出现一张卡
[followed-new] PASS 全部落在 3 个月窗口内
[followed-new] PASS 按发行日期倒序
[followed-new] PASS

[browse-followed] 关注新歌 说明「最近 3 个月 · 24 首」 卡片 24 张 可见=True
[browse-followed] 关注新专 说明「最近 3 个月 · 16 张专辑」 卡片 16 张 可见=True
[browse-followed] 新专前 8 张: … | BELOVED / 明透 · 共 10 首(2 周前) | …
[browse-followed] 专辑卡 16 张,其中写真实曲数 16 张 / 回落采样 0 张
[browse-followed] PASS 新歌卡全是单曲、新专卡全是专辑
[browse-followed] PASS 每张卡片都有悬停播放/更多按钮
[browse-followed] PASS 新歌速递卡也有悬停播放/更多按钮
[browse-followed] PASS 新歌速递卡也带歌曲菜单数据
[browse-followed] PASS 每张卡片都有时间徽章
```

`--browse-ops`（离线）另外验证三类卡的手势与菜单：

```
[browse-ops] 歌单卡封面浮现 ▶ 与 ••• 两个操作圆钮
[browse-ops] 单曲卡封面也浮现 ▶ 与 •••(点击卡本身直接播放)
[browse-ops] 专辑卡封面也浮现 ▶ 与 •••(点击卡本身进专辑页)
[browse-ops] 单曲卡菜单 = 歌名 / 添加到歌单 / …
[browse-ops] 专辑卡菜单 = 专辑名 / 播放 / 打开专辑页 / 表演者 / ─ / 分享 / 复制链接
[browse-ops] 右键弹出歌单菜单
[browse-ops] 专辑卡右键/长按弹出专辑菜单
```

聚合前同一批数据是「24 格全被少数几张专辑占满」；先聚合再按两类各 24 张截断后，
同样的窗口能给出 24 张单曲卡 + 16 张专辑卡（数据随关注歌手发歌变化，数字会浮动）。
补全量曲目让总耗时从 0.8s 涨到 1.1s（16 张专辑，6 并发）。

## 6. 已知限制

- **必须登录**：任一平台未登录时区块整块隐藏（一次请求都不发）。两个都登录才有混合内容。
- 关注歌手越多越慢：QQ 侧 32 位 ≈ 32 次请求；关注上百位时首屏会明显变慢，缓存 30 分钟内只付一次。
  专辑卡还会各多 1 次全量曲目请求（实测 16 张 +0.3s）。
- 只认发行日期：日期缺失的曲目直接丢弃（无法判断是否在窗口内），所以个别独立音乐人的新歌可能漏掉。
- **全量曲目拉不到时回落采样**：卡片写「N 首新歌」、▶ 播采样曲目 —— 宁可明说是新歌数，
  也不把采样数冒充成专辑曲数。实测 16/16 都能拉到。
- **「关注新专」只收同专辑命中 ≥2 首的**：一张新专辑只被采样到 1 首时，那首留在「关注新歌」里，
  不会为了它单独生成一张专辑卡。要覆盖全量新专辑得另拉歌手的专辑列表（多一倍请求），当前没做。
- 全量曲目是**按专辑接口原样**拿的（含老歌、可能含 VIP/无版权曲目），播放时由播放器按权限处理。
- 不是实时推送：两家都没有可用的桌面推送通道，本质是 30 分钟粒度的轮询。
