# QQ 歌手页:歌曲与专辑列表

代码:`Services/QQMusic/QQMusicApiClient.cs`(歌手/专辑段),UI:`ArtistViewModel`(歌手页)、
`ArtistAlbumsPageViewModel`(全部专辑页)。探针:`--qq-albums [关键词]`。

---

## 1. `GetAlbumList` 的 `order` 与歌曲那套**不是**同一套语义

用户反馈(2026-11-13):QQ 源「鸣潮先约电台」的专辑只看到《待春归》(2026-07-29),
官方已经发了《放课后时光》(2026-11-13),滚到下面才加载出来。

实测同一 `singerMid`(`002wbPaC0L9C3A`)各 `order` 的第一页(`begin=0, number=30`):

| order | 首条 | 第 12 条 | 结论 |
|---|---|---|---|
| **0** | 2026-11-13《放课后时光》 | 2026-07-29《待春归》 | 按发行时间倒序,最新在前 ✅ |
| 1 | 2026-07-29《待春归》 | 2026-02-27《待到风雪止息》 | **把最近十几张甩到列表末尾** ❌ |
| 2 / 3 | 与 0 相同 | 同 0 | 实测一致,但只有 0 有明确来源,别用 |

`order=1` 的四页拼起来看得很清楚:第 1~2 页从 2026-07-29 一路倒到 2025-03-16,
然后**又绕回** 2026-09-09、2026-08-21、2026-08-20 —— 也就是说 2026-07-29 之后发布的
十几张被排在了**第 60 条之后**。这就是"最新专辑要滚很久才出现"的全部原因,
和分页/缓存无关,是排序档选错了。

> 对比:`GetSingerSongList` 是 **1=热度、2=按发行时间倒序**(见
> [followed-new-songs.md](followed-new-songs.md))。两个接口字段同名、含义完全不同,
> 抄参数时必须逐个实测。

修法:`GetArtistAlbumPageAsync` 默认 `order: 0`;歌手页横向区(`GetArtistAlbumsAsync`)
与全部专辑页都走默认值。

## 2. 单页上限 30(`number` 传 50 也只回 30)

`number=50` 实测只回 30 条。老代码 `take = Math.Min(50, limit - all.Count)` 于是
第一页回 30 < 50 ⇒ 判定"没有下一页" ⇒ **歌手页横向区只有 30 张专辑**,注释写的 50 从来没兑现。
现在按 30 取、循环补齐(`limit=50` 会拉两页;第二页 `number=20` 服务端仍可能回 30,
所以实际可能略超 limit —— 无害)。

分页连续性实测(`order=0`,四页 30/30/30/2):92 条**无重复、无跳空**,日期严格递减到
2024-05-17。`order=1` 同样 92 条无重复,只是顺序错。

## 3. 其它已知点

- **曲数要单独补**:`GetAlbumList` 的 `totalNum` 恒为 0,只能批量 `GetAlbumSongList`
  (`num=1` 只为读 `data.totalNum`,30 个子模块一次请求 ≈0.23s)。见
  `GetAlbumSongCountsAsync`。
- **专辑/单曲分流**:`albumType` 字符串,含 `EP`/`单曲` 归"单曲与 EP",其余归"专辑"。
  最新专辑通常是"录音室专辑",不会被分流吃掉。
- **榜单/歌单的 total 不可信**:`uniform_get_Dissinfo` 会剔无效条目但 `total` 照算,
  与这里无关,见 [netease-login.md](netease-login.md)。

## 4. 回归判据(`--qq-albums`)

```
[qq-albums] order=0 begin=0 ... 首=2026-11-13 放课后时光
[qq-albums] order=1 begin=0 ... 首=2026-07-29 待春归      ← 旧行为,不该再被默认使用
[qq-albums] number=50 实回=30
[qq-albums] 歌手页 0. 放课后时光
[qq-albums] 专辑页 0. 放课后时光
```

歌手页/专辑页首条必须是最新发行的那张;若又变成 `待春归`,说明有人把 `order` 改回了 1。
