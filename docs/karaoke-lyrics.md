# 逐字歌词渲染（KaraokeText）

> 2026-11-18 落地。控件 `src/ALyricEase/Controls/KaraokeText.cs`，只接在**播放详情页**的
> `DetailLyricItemTemplate` 和桌面歌词窗口上。手机/紧凑模板使用整行高亮。
> 数据来源见 [netease-api/05-lyric-yrc.md](netease-api/05-lyric-yrc.md)。

QQ 音乐通过 `GetPlayLyricInfo` 请求 QRC。解码后，每个词尾的 `(起始毫秒,时长)` 转为
`LyricWord`，翻译按行时间合并。旧 QQ 歌词缓存仍可离线读取，联网播放时会尝试升级。
运行 `dotnet run --project src/ALyricEase.Headless -- --qq-karaoke-real 97773` 可用线上歌词验证详情页高亮。

## 1. 画法：两个 TextLayout + 几何裁剪

一个 `TextBlock` 只有一个 `Foreground`，做不出一句里"前半亮后半暗"。候选三条路：

| 方案 | 代价 | 结论 |
|---|---|---|
| 双 `TextBlock` 叠放 + 上层 `OpacityMask` 渐变 | 走离屏层 + 图像过滤，和歌词行挂 `Effect` 同一条贵管线 | ✗ |
| 每字拆 `Run`/多个 TextBlock | 每帧重建排版，折行/字距会抖 | ✗ |
| **自绘 `Control`，同一文本画两遍，上层 `PushClip`** | 一行文字画两遍，无离屏层 | ✓ |

`TextLayout` 只建一次（文本/字号/字重/可用宽度变化才重建）。`Render`：

1. 画 `_dimLayout`（未唱色，默认 `#66FFFFFF`）铺满整行；
2. 逐 `TextLine` 算"已经亮到哪个 x"，`PushClip` 后用 `_litLayout`（白色）再画一遍。

折行天然支持：每个 `TextLine` 单独算右边界，第二行从 x=0 继续。

## 2. 位置：采样只作目标，推进靠 Stopwatch

播放器的 `PositionChanged` 约 200ms 一次（Windows 是 `WindowsMediaPlayer` 的线程池 Timer 轮询，
Android 是 ExoPlayer 的台阶值）。这条采样链有**两个必须假设会发生的缺陷**：

1. **台阶**：位置不是连续的，一次跳 200ms；
2. **回退**：采样会拿到比上一拍更小的值。`PlayerViewModel` 为此专门留了 `BackwardNoiseMs = 1500`
   的容差（否则进度条秒数会来回跳），但**传给歌词的是未过滤的原始值**。

所以 `KaraokeText` 里"采样"和"渲染位置"是分开的：

- `_sampleMs` / `_sampleAt`：最近一次采样及其到达时刻，只用来算**目标值**
  `target = _sampleMs + (now - _sampleAt)`；
- `_renderedMs`：真正画出来的位置，按帧用 `Stopwatch` 的真实间隔推进，再按比例吸收与 `target`
  的偏差（落后 12%/帧 ≈ 0.15s 追上；超前 0.5%/帧 ≈ 3s 慢慢消化）；
- **任何情况下都不后退** —— 逐字亮区往回走比短暂停顿更刺眼；
- 偏差 > 800ms 才当成 seek/切歌直接对齐。

> ⚠ **别改回"采样到达就把渲染位置设成采样值"**。那是第一版写法，看起来最直观，实际每 200ms
> 会把亮区往回拽一次（实测最大回退 391.8ms ≈ 两个字），桌面歌词上就是"每句都卡一下 / 随机卡住"。
> 详情页只按整行变亮，同样的回退看不出来 —— 这个问题只在逐字上暴露。

### 帧链看门狗

`RequestAnimationFrame` 是"一帧一次"的：`OnFrame` 里 `InvalidateVisual()` + 重新注册，靠自续。
只要某一次 tick 被跳过（窗口被遮挡、系统挂起、控件正在测量），链就永久断掉。第一版还用
`_looping` 标志表示"正在跑"，于是所有重启尝试都被 `if (_looping) return` 短路 —— 亮区就此冻住，
直到暂停/切行把状态翻一次。

现在拆成两个独立状态：

- `_pacerHeld`：是否持有 `UiFramePacer`（管 UI 线程唤醒）；
- `_framePending`：是否已有一帧在排队（管渲染链）。

`Kick()` 在选中行、逐字数据、播放状态、页面状态变化和控件挂载时同步帧泵。
播放中的当前逐字行收到采样时调用 `RequestFrame()`，已有帧在排队时直接返回。

### 暂停、切行、seek

- `!CanRun`（暂停 / 非当前行 / 页面收起）时没有插值可言：采样到达**直接对齐** `_renderedMs`，
  否则暂停时拖进度条画面不跟；
- `IsActive` 刚变 true：偏差 > 800ms 才对齐（首次变当前行通常是 0 → 30s），相邻行切换保持连续；
- `StopLoop()` 会把 `_frameAt` 清零，恢复后第一帧只校正偏差、不按暂停时长往前跳；
- 时间轴：`LyricViewModel.SyllablePositionMs = positionMs + doc.Offset`，与 `FindIndex` 的
  offset 语义一致。

## 3. 只对当前行启用

控件有两个开关：

- `IsActive` 绑定 `$parent[ListBoxItem].IsSelected` —— 只有选中的那行进入双色 + 帧循环。
  绑定失败时控件会**静默退回单色**，所以探针必须用像素数验证，不能只看属性。
- `IsPlaying` 绑定 `LyricViewModel.IsPlaying`（由 `PlayerViewModel.OnIsPlayingChanged` 同步）。

非当前行和普通 LRC 行只保存进度采样。切到有逐字时间的行时，控件从最近的采样开始绘制。
只有已挂载、页面展开、正在播放的当前逐字行申请动画帧。暂停时拖动进度条会立即重绘当前逐字行。

⚠ 同一套控件也用在桌面歌词（居中、`TextAlignment=Center`），那里的行起点 `TextLine.Start` 不为 0 ——
它是"距段落左边的距离"，不是字符索引；字符索引用 `FirstTextSourceIndex`。详见
[desktop-lyrics.md](desktop-lyrics.md) 的"顺带修掉的老 bug"。

## 4. 文本源

- 当前行：用 **yrc 字拼接**（`LyricLine.SyllableText`），保证字级时间与字符索引严格对应；
- 其它行：用 LRC 原文（`Original`），显示与改动前一致。

两者只可能在括号上有差异（全角/和声括号），由 `LrcParser` 的对齐规则兜底 —— 对不上的行
干脆不给 `Words`，整行退回单色。

## 5. 回归探针 `--karaoke`

`KaraokeProbe` 用假歌词（5 个字 × 200ms）建真实 `LyricView` + detail 模板，按位置截图
统计亮像素：

```
0ms   lit=0     （一个字都没唱）
500ms lit=1643  （前 2.5 个字）
1200ms lit=3834 （整行唱完）
```

判据：0ms 必须为 0；500ms > 0ms；1200ms > 500ms；切到下一行后旧行 `IsActive=false`。

### 真实数据版 `--karaoke-real [id]`

合成假数据只能证明"控件会画"，证明不了"线上数据接得上"。`--karaoke-real` 直接走
`NetEaseApiClient.GetLyricAsync` → `LrcParser.Parse` → 真实 detail 模板，挑中间那条带字级时间的
歌词行，按行区域（`TranslatePoint` + `Bounds`）统计亮像素：

```
[karaoke-real] 实际渲染行 index=30 “谁说对弈平凡的不算英雄” 共 11 字
[karaoke:real30-before] lit=0     dim=7898
[karaoke:real30-mid]    lit=1011  dim=6919
[karaoke:real30-after]  lit=7031  dim=1001
```

注意两点：
- 只统计目标行控件自己的矩形。真实列表里其它行也是白字，全窗口统计会把它们算成"已唱"。
- 目标行可能不在首屏（ListBox 虚拟化不物化容器），探针会 `ScrollIntoView` 并沿候选行下退。

> ⚠ 探针里把 `ListBoxItem.Transitions = null` 再设 `Opacity=1`：选中行的 0.4→1 有 0.5s
> 过渡，同步探针等不到它结束，像素会被压在 ~0.42 导致阈值失真。

### 流畅度版 `--karaoke-smooth`

上面两个探针都是"给定位置截图"，**测不出位置推进是否连续** —— 而"桌面歌词每句卡一下"正是
位置推进的问题。`--karaoke-smooth` 用 `AvaloniaHeadlessPlatform.ForceRenderTimerTick` 手动驱动
render tick，按 16ms 一帧推进，每 200ms 喂一个**带 120~400ms 回退**的采样（真实播放器就这么抖），
统计同一控件内相邻两帧的渲染位置：

```
[smooth] 帧数=130 采样次数=10 其中回退采样=3
[smooth] 渲染位置 27 → 4049 (共 4022ms / 实际 4010ms,速率 1.003x)
[smooth] 每帧推进 p50=31.7 p90=35.5 max=43.4 ms
[smooth] 倒退帧=0（最大回退 0.0ms） 停滞帧=0
[smooth] 帧链仍在排队=True
```

同一段脚本把 `AdvancePosition` 换回旧行为（采样硬对齐）时：

```
[smooth] 倒退帧=6（最大回退 -391.8ms） 速率 0.949x
[smooth] FAIL: 亮区倒退 6 帧
```

`--karaoke-smooth` 还有第二段"切行"：3 个 `KaraokeText` 轮流 `IsActive`，只比较**同一控件**的
相邻帧（跨控件比位置没有意义），验证换行本身不会把亮区拽回去。

## 6. 已知遗留

- **未唱色亮度**：当前行未唱 `#59FFFFFF`（35%），和下一行的整体 40% 比较接近，层级靠"已唱部分"撑。
  觉得不够突出就调 `KaraokeText.DimBrush`。
- 桌面歌词的正文也是这个控件（`TextAlignment=Center`），但没有 `IsPageActive` 绑定 ——
  那里靠 `IsPlaying` + attach/detach 停泵。

## 7. 某首歌"没效果"怎么查

按顺序看，前两步能覆盖绝大多数情况：

1. **先看数据有没有逐字**：`dotnet run --project src/ALyricEase.Headless -- --yrc <songId> --after-search`。
   `--after-search` 不能省：真实 App 是"先搜索再播歌"，搜索会置位全局 `_wafBlocked`，
   而歌词曾经跟着它跳过加密通道（详见 [netease-api/05-lyric-yrc.md](netease-api/05-lyric-yrc.md) §8）。
2. **再看缓存版本**：`%LOCALAPPDATA%\ALyricEase.Data\cache\music\l-*.lyrics` 首字节必须是 `3`。
   v2 是"字段在但 yrc 恒空"的坏缓存，会自动作废重取；如果手动看过发现 v3 里 `yrc` 仍为空，
   说明那次是明文回落 —— 明文结果现在**不写缓存**，下次播会重试。
3. **最后才怀疑渲染**：`--karaoke-real <songId>` 跑一遍，像素不涨才是控件问题
   （重点看 `IsActive` 绑定和 `Words` 是否为空）。
