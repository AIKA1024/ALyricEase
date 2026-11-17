# QQ 歌单分页:滑到底不加载剩下的歌(已修,2026-11-17)

## 现象

用户报:「QQ 音乐的歌单滑到底下不会懒加载加载剩下的歌」。
1000+ 首的歌单(point 位于**歌单详情页**,左侧 QQ 分组点进去)只显示开头几百首,滚到底没有任何动作。

## 根因:`song_begin` 是**原始列表下标**,不是"已返回条数"

`uniform_get_Dissinfo` 会**剔掉失效/下架条目**,但 `total_song_num` 仍按原始总数下发
(旧记录「恒少于标称」说的就是这件事)。实测《听歌吧》(标称 1082 首):

| 请求 | 返回 | hasMore(旧判据) | hasMore(现判据) |
|---|---|---|---|
| begin=0, num=300 | **295** | **false ← 病灶** | true |
| begin=300 | 300 | — | true |
| begin=600 | 300 | — | true |
| begin=900 | 182 | — | false |
| 合计 | **1077**(与标称差 5 首失效条目) | 只拉到 295 | 拉齐 |

旧代码的终止判据是 `pageSonglist.Count >= pageSize && (total <= 0 || begin + count < total)`:
首页只回 295 条 ⇒ 第一个条件就不成立 ⇒ 判定"到底" ⇒ 后 787 首**再也拉不到**。
300 首以内的歌单不受影响,所以长期没被发现。

两种推进方式的实测对照(`--qqpage 2660913991`):

- **按返回条数推进**(`begin += 实际条数`):1082 行但只有 1077 个唯一 id ⇒ 有 5 首重复
  ⇒ 说明 `song_begin` 是原始下标,重复的正是被剔掉条目留下错位。
- **按请求页长推进**(`begin += 300`):1077 行 / 1077 个唯一 id,与逐位顺序完全一致。

⇒ 结论:**页长推进 + 以标称总数为到底判据**。空页另算兜底(整页条目全部失效时不会原地打转)。

## 修法

`QQMusicApiClient.GetPlaylistTrackPageAsync`:

```csharp
var hasMore = pageSonglist.Count > 0
    && (total > 0 ? begin + pageSize < total : pageSonglist.Count >= pageSize);
```

`PlaylistViewModel`(QQ 歌单页改成真分页,不再"一次拉全量再上屏"):

- `_qqPaging`(`PlaylistId` / `NextBegin` / `HasMore`)是页面级游标;`ClearTrackRows()` 里连同
  单飞任务一起作废 —— 换歌单/离页必须清,否则会把旧歌单的下一页接到新页面上。
- 打开时只取**首页 300 首**上屏,`IsBusy` 立刻落下;余下几页由 `EnsureQqPlaylistPagesLoadedAsync`
  串行补完(单飞)。滚动到底(`PlaylistView` 剩余 < 500px → `LoadMoreAsync`)走同一条路径,
  失败时游标停在失败页、滚动即重试。
- 展开排序/筛选:筛选要覆盖整张歌单,`LoadAllTracksForFilterCoreAsync` 对 QQ 页也按页拉完。
- 「播放全部」:QQ 的播放队列就是"已物化行",续页没完就先 `await` 补齐,否则只播首屏那 300 首。
- 离页快照:`PlaylistPageCacheData.QqPaging` 存游标,返回时接着续(否则返回后行数停在离页那一刻,
  再滚也长不出来)。

## 回归探针

`--qq-pl-paging <tid>`(需要本机 QQ 登录态;`2660913991` 是 1082 首的实测样本):

```
[qq-pl] 基线 begin=0: 295 首, total=1082, hasMore=True
[qq-pl] 基线 begin=900: 182 首, total=1082, hasMore=False
[qq-pl] 基线合计 1077 行 / 1077 首去重, 5 页
[qq-pl] 首屏 295 行 @ 329ms(含续页总耗时 1326ms, 滚动触发=True)
[qq-pl] 落地: 行=1077 去重=1077 队列=1077 trackIds=0
[qq-pl] PASS
```

判据(任何一条红都说明分页又坏了):行数 == 基线唯一曲目数、无重复 id、行顺序与基线逐位一致、
末行 == 歌单最后一首、`_queueSongs` 行数 == 行数、首屏严格早于全量完成、拉完再滚行数不变。
纯接口侧的语义对照另用 `--qqpage <tid> [num]`(两种推进方式各拉一遍,比较 id 集合)。

## 边界

- **榜单页(`Playlist.TopId > 0`)不走这里**:榜单曲目是 `ToplistInfoServer/GetDetail` 一次取
  `limit` 首(默认 100,`GetToplistSongsAsync`),没有"续页"概念,本次未改。
- 头部曲数仍是列表接口的标称值(1082),行数会少几首 —— 失效条目本来就不该出现在列表里。
- 首页上屏之后,剩余页是**自动补完**的(不等滚动),滚动只是补页的另一个触发口;
  1000 首 ≈ 4 个请求,比"首屏干等全量"体验好,也不改变播放/筛选语义。

## 被剔掉的条目能不能"显示 + 禁用"?(2026-11-17 实测:不能)

用户问过"失效/下架的歌曲信息还在吗,能不能显示出来但禁用、官方是怎么做的"。逐条通道试过:

| 通道 | 参数 | 结果 |
|---|---|---|
| `uniform_get_Dissinfo` | `song_num=2000` | 1077 条(仍然缺 5) |
| `uniform_get_Dissinfo` | `onlysonglist=1` | 295 条(只回一页) |
| `CgiGetDiss`(老详情) | `song_num=2000`,userinfo/tag/orderlist 给全 | 1077 条 |
| `CgiGetDiss` | `song_num=300` | 295 条 |
| 老 qzone `fcg_ucc_getcdinfo_byids_cp` | 匿名 | 1069 条(它自己的 `total_song_num` 也是 1069) |
| `music.playlist.PlaylistInfoServer/GetDetail` | — | code=500003,模块不存在 |

再验证"是不是整页截断":把前 300 位按 **15 条**切页,合计仍是 **295**,且 `begin=0` 那页只回 **10** 条
⇒ 缺的 5 首落在最前面十几个位置,服务端是**条目级剔除**,换页长也捞不回来。
(附带结论:`songlist[].status=31` 不是可用性标记 —— 该组 81 首全部有 320k、77 首有 flac、
`action.alert` 恒为 11,别拿 status 当"已下架"。)

所以:

- **没返回的条目没有任何可展示字段**(连歌名都没有),从零开始无法"显示+禁用"。
- 官方客户端里那种灰色"已下架"行,来自**它自己的歌单副本**(本地数据库/云同步的曲目 id 列表),
  详情接口拿不到的用占位行补;曲库层面"无版权但仍存在"的歌本来就带权限标记,那类才是有信息可灰显的。
- 我们能复刻的只有:①服务端返回了但当前账号不可播的(付费/VIP/无版权)→ `PlaybackAvailability`
  已经整行禁用,缺的是可见角标/提示;②靠自己的离线缓存/页面快照给"以前见过的歌"补占位 —— 首次打开补不出来,
  位置也只能近似(只在缺口的区间内知道少了 N 条,定位不到具体下标)。

探针:`--qqdiss <tid>`(逐通道列条数 + 前 300 位按 15 切页的合计)。
