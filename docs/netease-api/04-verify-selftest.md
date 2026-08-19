# 端到端验证清单（--selftest）

> 验证入口：`ALyricEase/SelfTest.cs`，命令行 `--selftest` 触发。
> 无头模式跑，不依赖 UI。用于回归确认每个 API 通道可用。

---

## 1. RunAsync（API 通道冒烟，覆盖最广）

| 步骤 | 验证点 | 通过标准 |
|---|---|---|
| 固定 key weapi 交叉验证 | `EncryptWeapiWithKey("{\"s\":\"晴天\",\"type\":1}", "abcdefghijklmnop")` | 与 Python/Node 参考输出一致 |
| LRC 单元自检 | 多标签/排序/offset/翻译合并 | 行数 3、翻译合并、二分定位正确 |
| 匿名注册（weapi） | `EnsureAnonymousAsync` | 加密通道可用则成功；被 WAF 拦则打印回落 |
| 搜索 | `SearchAsync("晴天 周杰伦", 5)` | 返回结果，字段含 id/name/artist/时长 |
| 播放地址 | `GetPlayUrlAsync(id, "standard")` | URL 非空（免费曲）；或如实报告 null |
| 歌词 | 遍历前 5 首找有时间轴歌词 | 解析出行、首句时间、二分 @60s 定位 |
| 公开歌单全量 | `GetPlaylistDetailAsync(19723756)` | 返回全量曲目，首曲可读 |
| 用户资料 | `GetUserProfileAsync()` 未登录调用 | 抛"未登录"（符合预期） |
| 推荐区块 | 推荐歌单/热歌榜/猜你喜欢/每日推荐 | 各返回卡片；每日推荐未登录应空 |

## 2. RunPlayAsync（M2+M3 播放链路）

| 步骤 | 验证点 |
|---|---|
| 搜索「周杰伦 晴天」 | 优先挑"有时间轴歌词 + 可播放"的歌 |
| 播放 | 进入 `Playing`、进度前进 |
| 暂停/恢复 | `IsPlaying` 翻转 |
| 音量 | VM 写入 → 底层生效 |
| 拖动 seek | 底层位置跳到目标附近 |
| 歌词联动 | 当前句 `CurrentIndex >= 0` |

## 3. RunLeakTestAsync（内存泄漏）

| 步骤 | 验证点 |
|---|---|
| 搜索 → 8 首可播曲目 | 播放地址逐首验证 |
| 连播 | GC/WS 不随播放数线性增长 |
| 打开大歌单（懒封面） | 建 VM 不拉封面，内存不暴涨 |
| 单曲长播 60s | 每 5s 采样，确认无按时间累积泄漏 |

---

## 回归要点

- **加密通道被 WAF 拦时**：`[anon] 匿名注册失败 → 走明文回落` 是预期输出，不代表功能坏。
  此时加密路径静默跳过，所有结果应仍来自明文 GET。
- **字段差异**：明文 song/detail 返回 `artists/album/duration`（`MapLegacySong`），weapi 返回 `ar/al/dt`
  （`MapSearchSong`），两套 DTO 不可混用。搜索明文回落走 `/api/cloudsearch/pc`，字段与 weapi
  一致，复用 `MapSearchSong`（`/api/search/get/web` 已无 `picUrl`，弃用）。
- **VIP 曲**：`GetPlayUrlAsync` 对 VIP 曲目应返回 null（明文 `-110`）或试听，播放器如实降级。
