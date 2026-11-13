# QQ 音乐的"私人FM"对标物 + 双源逐首交替

用户先问"QQ 音乐有没有类似网易云私人FM 的东西",确认后要求**不做切换按钮**,
"放 1 首就换一个源"。现在私人FM 是两源交替:网易云 → QQ → 网易云 → QQ …

---

## 1. QQ 侧取歌通道(实测 `--qq-fm`)

| 形状 | 回包 |
|---|---|
| `music.radio.RadioServer / get_radio_track`(radioId=99 / 不传) | `code=500003 subcode=860100001` —— 通道拒绝(新版协议要客户端专属参数) |
| **`pf.radiosvr / GetRadiosonglist`**(`{id:99, firstplay:1, num:10}`) | **10 首**,连打两次 9/10 首、**重叠 0** ✅ |
| `music.recommend.TrackRelationServer / GetRadarSong` `ReqType=1` | `code=23332`,无歌 |
| 同上 `ReqType=3` | 10 首(另一路推荐,内容偏冷门/日系) |

即 QQ 的私人FM = **猜你喜欢电台 id 99**,走老电台模块 `pf.radiosvr/GetRadiosonglist`;
`firstplay=1` 表示"重新开一轮",服务端按账号口味随机给,连续调用不重复。
外部资料也一致:QQ 客户端里「私人FM」与「每日30首」共用同一套音乐偏好。

**回包结构坑**:`data.track_list[]` 的条目是**平铺曲目本身**(顶层就有 `id/mid/name/singer/album/file`),
不像歌单那样再包一层 `track_info`。按 `track_info` 建模会得到 0 首(第一版就踩了)。

## 2. 播放器怎么交替

`PlayerViewModel` 里 FM 改成两个缓冲 + 轮次:

- `_fmNetEase` / `_fmQq` 各一个 `Queue<Song>`;`_fmNextFromQq` 记录下一首该谁出。
- `TakeFmSong()`:轮到谁就取谁,取到后翻转轮次 ⇒ **相邻两首必然换源**;
  轮到的那源暂时没货则由另一源顶上,轮次不动。
- `FillFmBufferAsync()`:**某源剩余 ≤1 首**就补(不是等 0)。等空了再补已经来不及 ——
  轮流到它时缓冲已空,只能连续从另一源顶歌,实测出现过"QQ QQ QQ"。
- `PlayFmNextAsync()` 在校验队列长度前先看 `FmTurnEmpty`,轮到的那源见底就先 `await` 补货,
  再决定要不要从另一源顶。
- 起播一次取 4 首(网易云 1 / QQ 1 交替),之后边播边补;FM 仍是无限流,
  历史只留 5 首供"上一首"。

QQ 客户端新增 `QQMusicApiClient.GetPersonalFmAsync(ct)`(一批 10 首,复用 `MapTrack`);
网易云沿用 `/api/v1/radio/get`(一批约 3 首) —— 两边批次大小不同,靠上面的补货阈值抹平。

## 3. 只登录一个源也能用

- 两个都没登录:提示"登录后收听私人FM"(行为同以前)。
- 只登录一个:另一源取歌直接返回空,`TakeFmSong` 自动降级成单源无限流,不报错。

## 4. 回归

```bash
--qq-fm     # 五条候选形状各打一次;打印曲目数与连打两次的重叠数
--fm-mix    # 真实登录态起播 + 连按 6 次下一首:每次实际切到的 Source 必须与上一次不同
--personal-fm  # 离线页面探针(布局/入口/空态,不触网)
```

`--fm-mix` 实测(2026-11-13,两源均已登录):

```
QQ 直取一批=10 首
启动后 队列=4      网易云 → QQ → 网易云 → QQ
连按 6 次下一首    QQ → 网易云 → QQ → 网易云 → QQ → 网易云(每次都与上一次不同源)
QQ 取址《READ MY LIPS》= OK (aqqmusic.tc.qq.com/M500...mp3)
PASS
```

判据里额外验了 **QQ 曲目能取到播放地址**:不然"交替"只是把不可播的行塞进队列。
