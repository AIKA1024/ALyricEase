# 端点目录

> 全部端点以 `NetEaseApiClient.cs` 为准。每个端点标注：方法 / 路径 / 是否需要登录 / 是否需加密。
> 响应样例为 2026-08 实测抓包（已省略长字段）。
>
> 通用约定：
> - Base `https://music.163.com`
> - 统一带 `User-Agent` + `Referer: https://music.163.com`（省略不重复写）
> - 加密 POST 路径带 `?csrf_token=`（weapi），eapi 不带
> - 明文 GET 大多匿名可用；标注"需登录"才需要 `MUSIC_U`

---

## A. 匿名会话

### A1. 匿名注册（weapi 加密 POST）

```
POST /weapi/register/anonimous?csrf_token=
Form: params=<weapi>&encSecKey=<weapi>
Body: {"csrf_token":""}
```

- 响应：`{"code":200}`，并**下发 `MUSIC_A` cookie**（Set-Cookie）。
- 本项目将 `MUSIC_A` 持久化 14 天，过期前不重复注册（`EnsureAnonymousAsync`）。
- ⚠️ 本机加密通道被 WAF 拦时此端点返回空 body → 走明文回落（多数明文端点其实不需要 MUSIC_A）。

---

## B. 搜索

### B1. 搜索（weapi 加密 POST，主路径）

```
POST /weapi/cloudsearch/get/web?csrf_token=
Body: {"s":"晴天","type":1,"limit":30,"offset":0,"csrf_token":""}
```

| 参数 | 值 | 说明 |
|---|---|---|
| `s` | 关键词 | |
| `type` | `1` | 1=单曲 |
| `limit` / `offset` | 分页 | |

响应（`SearchResponse`）：

```json
{
  "code": 200,
  "result": {
    "songs": [
      {
        "id": 186016,
        "name": "晴天",
        "ar": [ {"id": 6452, "name": "周杰伦"} ],
        "al": { "id": 11345, "name": "叶惠美", "picUrl": "https://p1.music.126.net/...jpg" },
        "dt": 269232,
        "fee": 8
      }
    ],
    "songCount": 168
  }
}
```

**字段注意：weapi 用 `ar` / `al` / `dt`。**

### B2. 搜索（明文 GET，回落路径）

```
GET /api/cloudsearch/pc?s=晴天&type=1&limit=30&offset=0
```

响应字段与 B1 **一致**（`ar` / `al` / `dt` + `result.songCount`），直接复用
`SearchResponse` / `MapSearchSong`，且 `al.picUrl` 齐全（匿名可用）。

> 曾用 `/api/search/get/web`（字段 `artists` / `album` / `duration`，即 `LegacySearchSong`）：
> 该端点 2026 年实测已不再返回 `album.picUrl`（只剩 `picId`），搜索页封面全空，故弃用。

```json
{
  "code": 200,
  "result": {
    "songs": [
      {
        "id": 186016,
        "name": "晴天",
        "ar": [ {"id": 6452, "name": "周杰伦"} ],
        "al": { "id": 11345, "name": "叶惠美", "picUrl": "https://p2.music.126.net/...jpg" },
        "dt": 269232,
        "fee": 8
      }
    ],
    "songCount": 168
  }
}
```

---

## C. 歌词

### C1. 歌词（weapi 加密 POST）

```
POST /weapi/song/lyric?csrf_token=
Body: {"id":186016,"os":"pc","lv":-1,"kv":-1,"tv":-1,"csrf_token":""}
```

| 参数 | 值 | 说明 |
|---|---|---|
| `lv` / `kv` / `tv` | `-1` | 取原版 + 翻译 + 罗马音 |
| `os` | `"pc"` | |

### C2. 歌词（明文 GET，回落路径）

```
GET /api/song/lyric?id=186016&lv=-1&kv=-1&tv=-1
```

两者响应相同（`LyricResponse`）：

```json
{
  "code": 200,
  "lrc":    { "lyric": "[00:00.00] 作词 : 周杰伦\n[00:01.00] 晴天..." },
  "tlyric": { "lyric": "[00:01.00] 晴天  ..." },
  "yrc":    { "lyric": "" }
}
```

- 无歌词曲目 `lrc.lyric` 为纯文本（无时间轴）或空。
- 本项目 LRC 解析在 `Services/Lrc/LrcParser.cs`。

---

## D. 播放地址

### D1. 播放地址（eapi 加密 POST，主路径）

```
POST /eapi/song/enhance/player/url/v1
Form: params=<eapi hex>
Body: {"ids":"[186016]","level":"higher"}
```

### D2. 播放地址（明文 GET，回落路径）

```
GET /api/song/enhance/player/url?ids=[186016]&br=320000
```

| 参数 | 值 | 说明 |
|---|---|---|
| `ids` | `[id]` | JSON 数组字面量 |
| `br` | `128000` / `320000` / `999000` | **明文路径必带 br**，不带返回空 |

**level → br 映射**（`LevelToBr`）：

| level | br |
|---|---|
| `hires` / `lossless` | 999000 |
| `higher` | 320000 |
| `standard` / 其他 | 128000 |

两者响应相同（`PlayUrlResponse`）：

```json
{
  "code": 200,
  "data": [
    {
      "id": 186016,
      "url": "https://m701.music.126.net/...mp3",
      "br": 320000,
      "fee": 8,
      "isTrial": false
    }
  ]
}
```

**关键行为（实测）**：
- 免费曲：匿名可得 `standard`(128k)；`higher` 也可能拿到。
- **VIP 专属曲返回 `url: null` 且 `code: -110`**（明文路径）；eapi 路径对 VIP 返回试听(30s, `isTrial: true`) 或 null。
- **海外 IP 直接 `url: null`** → 必须带 `X-Real-IP: <CN IP>`。
- 自动降级链：`higher` → 拿不到再试 `standard`。

---

## E. 登录用户

### E1. 用户资料（明文 GET，需登录）

```
GET /api/nuser/account/get
```

响应（`LegacyAccountResponse`）：

```json
{
  "code": 200,
  "account": { "id": 368236520, "vipType": 10, "anonimousUser": false, "paidFee": true },
  "profile": {
    "userId": 368236520,
    "nickname": "咸咸的鱼干",
    "avatarUrl": "https://p1.music.126.net/...jpg",
    "province": 440000,
    "gender": 1,
    "lastLoginIP": "119.2.250.159",
    "vipType": 10
  }
}
```

- 未登录：`code != 200` 且 `profile` 为空 → 本项目抛"未登录或 cookie 失效"。
- `vipType`: `0`=无, `10`=黑胶 VIP, `11`=黑胶 SVIP。

### E2. 用户歌单列表（明文 GET，需登录）

```
GET /api/user/playlist?uid=368236520&limit=50
```

响应（`LegacyUserPlaylistResponse`）：

```json
{
  "code": 200,
  "playlist": [
    {
      "id": 519899033,
      "name": "咸咸的鱼干喜欢的音乐",
      "trackCount": 1072,
      "coverImgUrl": "https://p1.music.126.net/...jpg",
      "specialType": 5
    }
  ]
}
```

**`specialType` 语义**：
- `5` = **"我喜欢的音乐"**（红心喜欢集合）→ 本项目用它识别红心歌单
- `20` = 年度歌单
- `0` = 普通歌单

### E3. 红心喜欢：取已喜欢集合

用 E2 识别的"我喜欢的音乐"歌单 id → 走 F1 的 v6 概览取 `trackIds`（全量红心 id）。
懒加载 + 单飞缓存（`EnsureLikedIdsAsync`）。

### E4. 红心喜欢：切换（明文 POST，需登录）

```
POST /api/song/like?csrf_token=
Form: trackId=186016&userid=368236520&like=true
```

| 参数 | 值 | 说明 |
|---|---|---|
| `trackId` | 曲目 id | **不是 `id`** —— 用 `id` 会返回 `code:400 参数错误`（已实测） |
| `userid` | 当前登录用户 uid | **不能省略** —— 缺它返回 `code:401 下架歌曲无法收藏`（值不严格校验，但参数必须在） |
| `like` | `true` / `false` | |

响应（成功）：

```json
{ "code": 200, "playlistId": 519899033 }
```

**注意走明文 POST 而非 weapi**（本机规避风控；明文 POST 表单格式即可）。

> ⚠️ 老资料常写 `id`/`like` 两参数，**是错的**——实测返回 400。
> 正确参数名 `trackId`/`userid`/`like`（与 HyPlayer LikeApi.cs 一致，2026-08 实测）。

---

## F. 歌单详情

### F1. 歌单概览 v6（明文 GET，登录态更多数据）

```
GET /api/v6/playlist/detail?id=519899033
```

响应（`PlaylistDetailResponse`）：

```json
{
  "code": 200,
  "playlist": {
    "id": 519899033,
    "name": "咸咸的鱼干喜欢的音乐",
    "trackCount": 1072,
    "trackIds": [ {"id": 186016}, {"id": 123456}, ... ],   // 全量 id，权威顺序
    "tracks": [ {...前段完整曲目, ar/al/dt...} ]           // 登录态约 150 首，匿名约 10 首
  }
}
```

**重要特性**：
- `trackIds` = **全量曲目 id，权威顺序**，可只凭它渲染列表骨架。
- `tracks` = 接口顺带返回的前段完整曲目（**登录态约 150 首，匿名约 10 首**）。
- ⚠️ `trackIds.Count` 可能 **> `trackCount`**（实测 1083 vs 1072）：`trackIds` 包含已删除/灰掉的
  曲目，`trackCount` 是有效计数。展示时建议以 `trackCount` 或请求 `/api/song/detail` 后按实际返回过滤。

### F2. 歌单全量曲目（综合流程）

```
1. F1 v6 概览 → trackIds + 前段完整曲目
2. 用 trackIds 减掉已有 → missing 列表
3. 分批调 G1 明文 /api/song/detail 补齐（每批 ≤100）
```

- 大歌单（如 1072 首）不必全拉；本项目支持增量：先上屏 trackIds 骨架 + 前段，滚动近底再按批补齐。
- 老路径 `/api/playlist/detail`（v1）只回前 ~150 首，已被 v6 取代。

### F3. 取歌单前 N 首（预览用）

```
GET /api/v6/playlist/detail?id=...
```
取 `tracks` 前 N 首；不足则从 `trackIds` 前 N 个用 G1 补齐。推荐页预览只拉前 6 首。

### F4. 歌单增删曲目（明文 POST，需登录）

```
POST /api/playlist/manipulate/tracks?csrf_token=
Form (application/x-www-form-urlencoded):
  op=del|add&pid=519899033&trackIds=[3382900119]&imme=true
```

| 参数 | 值 | 说明 |
|---|---|---|
| `op` | `del` / `add` | 删除 / 添加 |
| `pid` | 歌单 id | |
| `trackIds` | JSON 数组字符串 | **必须是 `[3382900119]` 或 `["3382900119"]`（JSON 数组）**；逗号分隔 `3382900119` 会返回 400「传入的歌曲ID错误」 |
| `imme` | `true` | 必须带 |

响应（成功，2026-08 实测删除 Calming Aroma）：

```json
{ "code": 200, "count": 1074, "cloudCount": 11 }
```

- **幂等**：重复删除已不在列表的曲目仍返回 200，但列表不减少（实测确认）。
- **必须带 `?csrf_token=`**（明文 POST 通道的固定后缀）。
- 官方 web 的 JS 里 `trackIds` 会先 `JSON.stringify(...)` 再提交，印证数组字符串格式。
- 本项目「红心切换」用的是更简单的 `/api/song/like`（见 E4），不需要这个接口；
  此接口用于**对任意歌单**增删曲目（如移除某首歌）。

### F5. 创建歌单（明文 POST，需登录 + `__csrf`）

```
POST /api/playlist/create?csrf_token=<__csrf>
Form (application/x-www-form-urlencoded):
  name=<歌单名>&privacy=10&type=NORMAL&csrf_token=<__csrf>
```

| 参数 | 值 | 说明 |
|---|---|---|
| `privacy` | `0` / `10` | 0 普通歌单，10 隐私歌单 |
| `type` | `NORMAL` | NORMAL/VIDEO/SHARED，UI 仅提供普通歌单 |
| `csrf_token` | `__csrf` 值 | **URL 与表单都要带**，值必须与 cookie 里的 `__csrf` 一致 |

响应（成功）：

```json
{ "code": 200, "id": 18329264369, "playlist": { "id": 18329264369, "name": "...", ... } }
```

- `id` 在**顶层**；部分通道也嵌在 `playlist.id`，客户端两处都兜底读。

**⚠️ 缺 `__csrf` 是本功能历史上一直失败的根因**：没有它时接口恒返回

```json
{ "code": 403, "message": "illegal request!", "msg": "illegal request!" }
```

实测该 403 **与 UA / Referer / Origin / csrf_token 参数完全无关**（六种组合全 403）——
服务端只认 cookie 里的 `__csrf`。补上后同一请求直接 200（2026-08 实测）。

### F6. 删除歌单（明文 POST，需登录 + `__csrf`）

```
POST /api/playlist/delete?csrf_token=<__csrf>
Form: pid=<歌单id>&csrf_token=<__csrf>
```

| 参数 | 值 | 说明 |
|---|---|---|
| **`pid`** | 歌单 id | **参数名是 `pid`，不是 `id`** —— 传 `id` 恒返回 `code:400 请求参数错误` |
| `csrf_token` | `__csrf` 值 | 同 F5 |

响应（成功）：`{ "code": 200, "id": 18329264369 }`

> 加密 weapi 通道的删除参数名沿用社区通用的 `id`（`/weapi/playlist/delete`），
> 与明文通道的 `pid` **不同** —— 这是两套通道少见的字段名分歧，改动时勿混用。

---

## G. 歌曲详情（批量）

### G1. 明文批量（推荐走这个）

```
GET /api/song/detail?ids=[186016,123456]
```

响应（`LegacySongDetailResponse`）：

```json
{
  "code": 200,
  "songs": [ { "id": 186016, "name": "晴天", "artists": [...], "album": {...}, "duration": 269232, "fee": 8 } ]
}
```

- 字段为 legacy 格式（`artists/album/duration`）。
- **单批 ≤ 100 首**（本项目 `GetSongsByIdsAsync` 硬性截断 100）。
- ⚠️ **明文 `/api/song/detail` 实测可用**；但 weapi 路径若被 WAF 拦截，用明文。

### G2. v3 详情（weapi 加密 POST，主路径）

```
POST /weapi/v3/song/detail?csrf_token=
Body: {"c":"[{\"id\":186016}]","csrf_token":""}
```

响应：`{"code":200,"songs":[{"id":...,"name":...,"artists":[...],"album":{...},"dt":...,"fee":...}]}`
（`SongDetailResponse`，字段是 `artists/album`）。

---

## H. 歌手 / 专辑

### H1. 歌手资料（明文 GET）

```
GET /api/artist/head/info/get?id=6452
```

响应（`ArtistDetailResponse`）：

```json
{ "code": 200, "data": { "artist": { "id": 6452, "name": "周杰伦", "avatar": "https://p1.music.126.net/...jpg" } } }
```

### H2. 歌手热门歌曲（明文 GET）

```
GET /api/artist/top/song?id=6452&limit=30
```

响应：`{"code":200,"songs":[ ...SearchSong 同构(ar/al/dt)... ]}`

### H3. 歌手专辑列表（明文 GET）

```
GET /api/artist/albums/6452?offset=0&limit=30
```

| 参数 | 说明 |
|---|---|
| `{id}` | 歌手 id，**路径形式**（query 形式的 `/api/artist/album?id=` 已废弃，恒返回 `code:400 参数错误`） |
| `offset` / `limit` | 分页，单页约 30 条 |

响应（`ArtistAlbumsResponse`）：

```json
{
  "code": 200,
  "more": true,
  "hotAlbums": [
    { "id": 147779282, "name": "最伟大的作品", "picUrl": "...", "size": 12, "type": "专辑" },
    { "id": 274336916, "name": "即兴曲", "picUrl": "...", "size": 1, "type": "Single" }
  ]
}
```

**关键点**：
- `type` 是**字符串**：`"专辑"` / `"Single"` / `"EP"`（**不是数字** 10/11/12）。
- `more: true` 表示还有下一页，需 `offset` 递增翻页（本项目翻页拉全，上限 50）。
- 本项目 `GetArtistAlbumsAsync` 已实现翻页；`ArtistViewModel` 按 `type == "专辑"` 分流到专辑/单曲EP 两个区块。

### H4. 专辑详情（明文 GET）

```
GET /api/v1/album/11345
```

响应（`AlbumDetailResponse`）：

```json
{
  "code": 200,
  "album": {
    "id": 11345, "name": "叶惠美", "picUrl": "...",
    "artists": [ {"id": 6452, "name": "周杰伦", "avatar": "..."} ],
    "size": 10, "publishTime": 1063843200000, "description": "..."
  },
  "songs": [ ...SearchSong 同构(ar/al/dt)... ]
}
```

---

## I. 首页推荐（明文 GET，匿名可用）

### I1. 推荐歌单

```
GET /api/personalized/playlist?limit=6
```

响应（`RecommendListResponse`）：

```json
{
  "code": 200,
  "result": [
    { "id": 3778678, "name": "热歌榜", "copywriter": "编辑精选", "picUrl": "...", "playCount": 6.3607476E7, "trackCount": 100 }
  ]
}
```

- `playCount` 用 **double**（接口返回科学计数法 `6.3607476E7`），long 反序列化会抛异常。

### I2. 猜你喜欢 / 新歌推荐

```
GET /api/personalized/newsong?limit=6
```

响应同 I1，但真实曲目嵌套在 `song` 字段：

```json
{
  "code": 200,
  "result": [
    { "id": 123, "name": "...", "picUrl": "...", "song": { "name": "...", "artists": [ {"id":..., "name": "..."} ] } }
  ]
}
```

### I3. 每日推荐歌单（需登录）

```
GET /api/v1/discovery/recommend/resource
```

- 响应（`RecommendResourceResponse`）：`{"code":200,"recommend":[...同 I1 结构...]}`
- **未登录返回 `code 301`** → 本项目由调用方隐藏该区块。

### I4. 每日歌曲推荐（需登录）

```
GET /api/v3/discovery/recommend/songs?csrf_token=
```

响应（`DailySongsResponse`）：`{"data":{"dailySongs":[ ...SearchSong 同构(ar/al/dt)... ]}}`
- 匿名/风控时返回空 → 由调用方兜底为 I3 的歌单卡片。

---

## J. 端点速查表

| 功能 | 主路径 | 回落 | 登录 | 备注 |
|---|---|---|---|---|
| 匿名注册 | `weapi/register/anonimous` POST | — | 否 | 下发 MUSIC_A |
| 搜索 | `weapi/cloudsearch/get/web` POST | `GET /api/cloudsearch/pc` | 否 | 回落字段同为 `ar/al/dt`(`/api/search/get/web` 已无 `picUrl`,弃用) |
| 歌词 | `weapi/song/lyric` POST | `GET /api/song/lyric` | 否 | `lv/kv/tv=-1` |
| 播放地址 | `eapi/song/enhance/player/url/v1` POST | `GET /api/song/enhance/player/url` | 否(匿名低码) | 明文必带 `br`；VIP `-110`；需 `X-Real-IP` |
| 用户资料 | — | `GET /api/nuser/account/get` | ✅ | |
| 我的歌单 | — | `GET /api/user/playlist?uid=` | ✅ | `specialType=5` 红心 |
| 红心切换 | — | `POST /api/song/like` | ✅ | 参数 trackId/userid/like(id 会 400) |
| 歌单增删曲目 | — | `POST /api/playlist/manipulate/tracks` | ✅ | op=del/add, trackIds 必须 JSON 数组字符串 |
| 创建歌单 | — | `POST /api/playlist/create` | ✅ | **必须带 `__csrf`**,否则恒 403 illegal request |
| 删除歌单 | — | `POST /api/playlist/delete` | ✅ | 参数名 **`pid`**(非 `id`),也要 `__csrf` |
| 歌单概览 | — | `GET /api/v6/playlist/detail?id=` | 登录态更全 | trackIds 权威顺序 |
| 歌曲批量 | `weapi/v3/song/detail` POST | `GET /api/song/detail?ids=` | 否 | ≤100/批 |
| 歌手资料 | — | `GET /api/artist/head/info/get?id=` | 否 | |
| 歌手热歌 | — | `GET /api/artist/top/song?id=` | 否 | |
| 歌手专辑 | — | `GET /api/artist/albums/{id}?offset=` | 否 | type 字符串:专辑/Single/EP |
| 专辑详情 | — | `GET /api/v1/album/{id}` | 否 | |
| 推荐歌单 | — | `GET /api/personalized/playlist` | 否 | |
| 猜你喜欢 | — | `GET /api/personalized/newsong` | 否 | song 嵌套 |
| 每日歌单 | — | `GET /api/v1/discovery/recommend/resource` | ✅ | 匿名 301 |
| 每日歌曲 | — | `GET /api/v3/discovery/recommend/songs` | ✅ | |
