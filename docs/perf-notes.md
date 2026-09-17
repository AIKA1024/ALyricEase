# 性能测量与结论（GPU / CPU）

从 `.workbuddy/memory/MEMORY.md` 外移（该文件有注入长度上限，只留结论与入口）。
Avalonia 框架层面的坑（事件类型、门控写法等）在 `docs/avalonia-tips.md`；内存设计审计在 `docs/memory-audit.md`。

## 一、怎么量 GPU

- 用 PDH 计数器 `\GPU Engine(*)\Utilization Percentage`：按 `pid_` 前缀过滤、**加总所有引擎**
  （3D / Copy / Compute / VideoDecode），并**丢弃第一个采样**（速率型计数器要两次收集才有值）。
  实现：`src/ALyricEase.Headless/GpuUsageSampler.cs`（P/Invoke `Pdh*`，实例结构体步长 24 字节）。
- 抓屏验证：`src/ALyricEase.Headless/ScreenCapture.cs`（GDI BitBlt + `DiffRatio`），用来判"某效果到底有没有真画出来"。
- ⚠ **GPU 计数器只在窗口可见且未被遮挡时才有值**。被遮挡时 DWM 不合成，读数恒 0 ——
  这种"数字很漂亮"的错误比崩溃更难查。探针必须 `Topmost = true`（会盖住桌面约两分钟）。
- 参考探针 `--np-gpu-real`（`src/ALyricEase.Headless/NowPlayingGpuProbe.cs`）：
  真 `TestMainWindow` + 真 `AppShell` + 真 `NowPlayingView` + 真 `PlayerViewModel`；
  网络不可用时封面注入合成位图、歌词直接灌进 `LyricViewModel`。同轮消融矩阵 + 抓屏像素验证。
  `ALY_NP_SIZE=WxH` 对齐用户实际窗口（填充率与面积成正比）。

## 二、结论：详情页 GPU 的大头是"在动"，不是"在画"

`AlbumCoverBackground` 的漂移动画（全屏色团横跨整窗移动，让合成器每帧重画整幅窗口）实测（1200x720）：

| 项 | 净代价 |
|---|---|
| 漂移本身 | **+7.53% GPU / +21% CPU** |
| 色团层**画出来** | 仅 **+1.77%** |
| 两个进度 RAF 循环 | ≈ **+0.25%** |

⇒ **代价与"看不看得见"无关，钱花在动上。**

根因：`NowPlayingOverlay` 在真实 `MainWindow.axaml` 里是**常驻**的（只靠 `RenderTransform` 移出窗外），
所以色团从启动起一直在动，哪怕详情页没打开。

修法：`AlbumCoverBackground.MotionEnabled`（默认 true）由宿主按
`MainWindow.NowPlayingMotionEnabled = ShowNowPlaying && AppState.DynamicBackground` 绑定。
停是**隐式**的、重开必须显式 `TryStartMotion()`，与 `ProgressRenderAnimator` 同一套约定
（`StopMotion()` 要复位 `_motionStarted` / bump `_motionGeneration`，漏掉就变单向开关）。

验收：首页门控 **1.00%** vs 强制开漂移 **8.16%** ⇒ 省 **+7.16% GPU / CPU 25.01% → 2.70%**；
漂移核对 ±0.06%（小于它的差异不算结论）。

另一个实测：歌词面板（`歌词·默认` 16.80% vs 详情页·默认 8.40%）逐行 `BlurEffect` 净代价 **+7.44%**
（抓到屏像素为证：噪声底 0.00%、消融差 4.58%）。只模糊近处两行 → **+5.51%**，收益小、观感变了，不值得。

## 三、三类"一直在动"的机制，成本模型完全不同

别用 GPU 读数否定任一者：**最小化时窗口不 present，GPU 必然归零**。

| 机制 | 跑在哪 | 最小化时 | 门控做法 |
|---|---|---|---|
| 自续订 RAF 循环（`ProgressRenderAnimator`） | UI 线程 | **回调仍送达**，每帧照写 `ScaleX` | `ShouldAnimate()` 里加 `IsHostPresentable()`；停止隐式、恢复要显式 `RestartFrameLoop()`；`_lastPresentable` 防抖 |
| 框架主题动画（`ProgressBar IsIndeterminate=True`） | 合成 + UI 线程 | **照转**，净代价 **2.60%** 单核 | `Infrastructure/IndeterminateAnimationGate` 附加属性 `IsActive="True"` |
| 组合动画（`AlbumCoverBackground`） | 渲染线程、帧驱动 | 不渲染 ⇒ 0 成本 | 见第二节 |

- 判据统一为 `IsHostPresentable`：`TopLevel.IsVisible` && 非 `WindowState.Minimized`。
  ⚠ **最小化时 `Window.IsVisible` 仍是 `true`**，只看它必然漏掉最小化。项目里已有两处实现，别写第三套。
- 不确定进度条共 5 处已全部门控：`PlaylistView`（补页）、`AccountView`（刷新）、`SearchView`（搜索中）、
  `LoginDialogView` ×2。新增时别忘了挂。
- 门控**必须只"压"不"吃"**：记住 XAML/绑定**想要**的值（`Desired`），不可显示时压 false、可见时还原；
  压时打 `Suppressing` 标记，否则自己写的值会被变更通知误当成"绑定改了值"而覆盖 `Desired`。
  离开视觉树时也置 false，重新挂回时由 `Apply` 还原。

## 四、写性能探针的坑

- **消融场景要写成"完整状态"**，不是"在上一个场景上再关一样"，行与行才可任意比较。
- **验收场景照应用自己的门控走**（读回控件状态再落实）。探针自己拨开关 ⇒ 应用改没改好都量出同一个数。
- **帧节奏要在 GPU 采样之后量**（请求帧本身会驱动平台出帧）。
- **"某循环是否在跑"要读它写的成果**（`ScaleX` / 合成视觉 `Translation`），别数帧数或看进程 CPU。
- **`CompositionVisual.Translation` 的 getter 读基础值**，不反映组合动画推到服务端的当前值
  ⇒ 拿它判"还在不在动"是瞎的（实测 13 个场景全是 0.00px）。
- **夹具缓存的控件引用会失效**：控件被重建后，沿用旧快照会指向已脱离视觉树的实例 ⇒ 写上去纹丝不动。
  每次从视觉树重取；"原值"只能从活的、样式已生效的控件读。
- **绕过控件直接对合成视觉 `StopAnimation` 会静默污染它之后的所有行**（控件的 `_motionStarted` 仍是 true，
  后面属性没变化 ⇒ 无变更通知 ⇒ 永不重新点火）。这种场景整体删掉。
- 真窗口可见时 CPU 大头**不是**这些循环：可见时进程 CPU **20~28% 单核**（Acrylic 模糊层 + 全窗合成），
  循环开/关配对差只有 ±1%（噪声内）。**要降可见时 CPU，方向不在这里。**
- **自测/探针进程里 `DispatcherService.Post` 的事件永远不跑**（依赖 Avalonia UI 泵，无人泵）。
  播放器事件断言两条路：① 无头 Stub 的事件是同步 raise，直接断言（首选）；② 真机进程要么泵
  `Dispatcher.UIThread.RunJobs` 循环、要么只做属性轮询——但**事件转发链必须有一条同步路径覆盖**，
  否则会漏掉"包装类构造时没接线"这类 bug（2026-09-17 CrossfadeAudioPlayer 漏 Wire primary：
  手动切歌走属性读所以有交叉，自然播完靠事件所以全哑）。
- **"手动路径好使、自动路径哑"优先查两条路径的依赖差异**：属性直读 vs 事件推送——分叉点就是嫌疑点。
