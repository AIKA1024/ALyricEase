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
| 条数上限 | 24 | 货架再多也没人滚到底，还拖慢封面解码 |
| 去重 | `(Source, Id)` 再 `(Source, Name, Artist, PublishDate)` | 合唱两边各一次；QQ 同一专辑偶尔下发多条同名 |

任一侧失败只影响自己（`NetEaseFailed` / `QqFailed` 分开记），两侧都失败时不写缓存 —— 下次进页面还要重试。

## 4. UI

- 位置：浏览页**第三块**「关注新歌」，排在「新歌速递」**之前**（关注的人比全站榜单更重要）；
  但它逐位歌手查、明显更慢，所以独立异步，不挡前三个货架。
- 独立异步加载：前三块先上屏，它自己在后台补（首屏不被 50 次请求拖住）。
  加载期间区块显示 16px 转圈 + 「正在汇总关注歌手的新歌…」；已有卡片时（刷新）不再显示这行。
- 标题右侧说明：`最近 3 个月 · 24 首`；没有命中时写「这段时间没发新歌」（和"一个歌手都没关注"分开）。
- 卡片复用 `BrowseCardViewModel` + `browse-card` 样式（Fluent 2：8px 圆角、层叠表面、hover 遮罩），
  但**右上角改为发行时间徽章**（`release-badge`，「昨天 / 3 天前 / 2 周前」）——新歌没有播放量，那个位置空着不如给时间。
- 点击播放，播放队列 = 当前 24 首（点哪首都能连续切）。

## 5. 回归

```powershell
# 数据链路(联网只读):歌手数 / 命中数 / 3 个月窗口 / 倒序 / 耗时
dotnet run --project src/ALyricEase.Headless/ALyricEase.Headless.csproj -c Debug -- --followed-new-songs

# 界面:真数据渲染 + 时间徽章 + 1120/620 无横向溢出,截图落临时目录
dotnet run --project src/ALyricEase.Headless/ALyricEase.Headless.csproj -c Debug -- --browse-followed
```

本机实测输出（2026-11-05）：

```
[followed-new] 歌手 网易云=17 QQ=32 失败(网易云=False QQ=False)
[followed-new] 合并 24 首(网易云 7 / QQ 17) 耗时 1.7s(cutoff 2026-08-05)
[followed-new] PASS 全部落在 3 个月窗口内
[followed-new] PASS 按发行日期倒序
[browse-followed] 区块 标题「关注新歌」 说明「最近 3 个月 · 24 首」 卡片 24 张
```

## 6. 已知限制

- **必须登录**：任一平台未登录时区块整块隐藏（一次请求都不发）。两个都登录才有混合内容。
- 关注歌手越多越慢：QQ 侧 32 位 ≈ 32 次请求；关注上百位时首屏会明显变慢，缓存 30 分钟内只付一次。
- 只认发行日期：日期缺失的曲目直接丢弃（无法判断是否在窗口内），所以个别独立音乐人的新歌可能漏掉。
- 不是实时推送：两家都没有可用的桌面推送通道，本质是 30 分钟粒度的轮询。
