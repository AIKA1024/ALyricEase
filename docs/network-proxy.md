# 应用网络代理(设置 → 网络 → 应用网络代理)

设置页那张卡片打开一个模态弹窗(`ProxySettingsDialogView`),三种模式:

| 模式 | 落盘 `ProxyMode` | 语义 |
|---|---|---|
| 跟随系统(默认) | 0 | `HttpClient.DefaultProxy` —— Windows 的 IE/系统代理设置,或 `HTTP_PROXY`/`HTTPS_PROXY` 环境变量。与改动前完全一致 |
| 不使用代理 | 1 | 直连。系统代理指向失效地址(代理软件已退出/换网络)时用 |
| 手动配置 | 2 | 指定 HTTP 代理 `host:port`,HTTPS 走 CONNECT 隧道,可带用户名密码 |

回归探针:`--proxy`(纯逻辑,不触网)、`--proxydialog`(三张渲染截图落 `%TEMP%\aly-proxydialog-*.png`)。

## 一、为什么 handler 拿的是一个"恒定实例"

用户改完代理不能要求重启,而 `HttpClientHandler.Proxy` 一旦 handler 建好就没有可靠的公开改法
(连接池还按 host 复用,改了也不会重连)。所以:

- `NetworkProxy.Instance` 是**整个进程唯一、永不替换**的 `IWebProxy`;
- 它每次 `GetProxy(destination)` 才去读 `NetworkProxy.Current` 快照;
- 用户保存时只 `NetworkProxy.Apply(snapshot)` 换个快照引用。

于是所有已建好的 `HttpClient` 立刻用上新出口,**不需要重建任何 client**。
反过来如果直接 `new WebProxy(...)` 塞给 handler,改设置就只能重启。

配套的两个约定:

- `Direct` 模式下 `GetProxy` 返回 **null**(.NET 的"不走代理"约定)。返回 `destination`
  虽然也算直连,但语义含糊,别那么写。
- `ProxySnapshot` 刻意是 **record class 而非 struct**:切换时只写一个引用(原子),
  读方最多看到"完整的旧快照"。struct 会在多线程下被读到 mode 已是手动、host 还是空串的撕裂态 ——
  第一版写成 `readonly record struct` + `volatile`,直接编译不过(CS0677)。

## 二、生效时机

- **新连接**:立即走新代理。
- **已存在的 keep-alive 连接**:继续连着旧出口,由 `PooledConnectionIdleTimeout`(默认 1 分钟)
  自然回收后换过来。UI 上没做"立即断开全部连接"——`SocketsHttpHandler` 没有清池 API,
  `ServicePointManager` 那套只管旧的 `HttpWebRequest`。

## 三、接入点(新增网络出口必须挂上)

一律给 handler 加 `Proxy = NetworkProxy.Instance`:

| 位置 | 用途 |
|---|---|
| `NetEaseApiClient` 构造函数 + 匿名注册那处临时 handler | 网易云接口 |
| `QQMusicApiClient`、`QQMusicQrLoginService` | QQ 接口 / 扫码 |
| `MusicCacheService.SharedHttp` | **音频流**(播放缓存) |
| `SongDownloadService.SharedHttp` | 歌曲下载 |
| `ManagedCoverImage` 的 `SocketsHttpHandler`、`CoverLoader.Http` | 封面 |
| `GitHubReleaseFetcher` | 更新检查(此前是裸 `new HttpClient()`) |
| `SmtcService.CoverHttp`(Windows) / Android 侧三处 | 任务栏封面 / 安卓下载与封面 |

### 反例:裸 `new HttpClient()`

裸 `new HttpClient()` 等于"跟随系统",用户选直连或手填代理时**不会生效**。
`GitHubReleaseFetcher` 原来就是裸的 —— 加代理功能时一起改了。

## 四、手动配置的输入宽容度

`NormalizeProxyHost` 允许用户整条粘贴 `http://127.0.0.1:7890/`:
剥协议前缀(`http/https/socks5/socks5h`)、去路径与尾斜杠、`host:port` 里内嵌的端口
在端口框留空时自动采用。IPv6 字面量(带方括号)原样保留,端口走端口框。

校验失败(主机空 / 端口不在 1-65535)**不落盘、不覆盖已生效配置**,只把原因写在弹窗里 ——
否则用户填错一次就把正在用的代理改坏了。

## 五、落盘与安全

`state.json` 里是 5 个平铺字段(`ProxyMode/ProxyHost/ProxyPort/ProxyUsername/ProxyPassword`),
在 `AppStateStore` 上聚合成一个 `ProxySnapshot Proxy` 整体读写,避免"模式已是手动但主机还是空"的中间态。

⚠ 代理密码与 Cookie 同级,**明文落盘**。本机个人配置文件,没做 DPAPI/Keystore 加密
(Android 上没有 DPAPI,引入跨平台加密库对这点收益不值)。

## 六、弹窗几何(对齐 Fluent 2 的 ContentDialog)

WinUI 3 的 ContentDialog 标准:`MinWidth 320 / MaxWidth 548 / 圆角 8(OverlayCornerRadius) /
描边 1 / 内边距 24 / 标题下间距 12 / 按钮间距 8`。本弹窗取 `MinWidth 360 / MaxWidth 520 / Padding 24 /
CornerRadius 8`,其余同上;正文用 13px 而非 Fluent 的 14px —— **全站弹窗都是 13**,单独改这一处会更不一致。

两处值得记的取舍:

- **模式选择:2026-11-11 改为 ComboBox**(原为 RadioButton 组,同日推翻)。
  Windows 设计指南的判据是:`RadioButtons` 用于"用户需要先看到全部选项再选、且每项需要解释";
  `ComboBox` 用于"选项名称自明、要省空间、默认值已适合大多数人"。本项是三个自明的枚举值,
  默认「跟随系统」覆盖绝大多数场景 ⇒ 下拉更省高度,弹窗从 ~460px 收到 ~200px(手动区收起时)。
  **代价**:Fluent 的 ComboBox 下拉项固定 32px 单行,每项的一句说明塞不进去
  (硬塞会让项高不齐、长文被截断),所以三项的说明文字已删 —— 想恢复说明就得回到 RadioButton 组。
  标签用"在上、控件撑满",与下方代理服务器/端口一致(标签在左会让同一弹窗出现两种标签风格)。
- **高度上限绑父容器** `MaxHeight="{Binding #ModalHost.Bounds.Height}"`,不写死像素。
  第一版写死 452,在 620 高的窗口里就把"用户名/密码"行切了一半;改后最小窗口(560)也能完整放下。

烟雾层沿用项目统一的 `#99000000`(Fluent 2 的 ContentDialog smoke 是 `#66000000`,40%)——
保持与其他弹窗一致优先于单点合规,要改就整站一起改。

## 七、卡片高度过渡(切「手动配置」时)

手动区展开会把卡片从 ~214 撑到 ~373,缩回时反向。做法与 `DownloadSongDialogView`
(查询卡片 → 音质列表)完全一致,不要另起一套:

- **布局一次跳到最终尺寸**,不逐帧改 `Height` —— 后者会让整棵子树每帧重排。
- 卡片拆三层:`DialogCard`(透明外壳,`MaxHeight` 挂在它身上,同时仍是
  `ModalVisibilityTransition` 那个 1.05 缩放的目标)→ `DialogSurface`(背景/描边/阴影)+ 内容 `Border`。
- `DialogSurface` 的**合成 Scale** 从 `(from.Width/to.Width, from.Height/to.Height)` 动画到 `(1,1)`,
  300ms、`SplineEasing(0.215, 0.61, 0.355, 1)`,`CenterPoint` 取卡片中心 ⇒ 上下对称伸展。
  文字不参与缩放,不会被压扁。
- **内容层在动画前半段(0→50%)完全隐藏**:放大时背景还小,内容(尤其刚展开的手动区)
  会露在卡片外;后半段随缩放到位淡入。缩小时同理,否则按钮会先跳到新位置再等背景缩回来。

两个坑:

- **中途反向切换**(用户来回试三个选项)不能从上一次的终值硬跳。`CompositionVisual.Scale`
  的 getter 读的是**基值(终值)**,量不到实时值,只能自己按同一条曲线反推当前尺寸当新起点。
- **收尾不能用 `DispatcherTimer`**:它依赖 dispatcher 主循环,无头回归里 `RunJobs()` 不推进它,
  内容会永远停在透明状态(实测卡在 386ms 不复位)。改成 `Task.Delay(MorphDuration)` +
  `Dispatcher.UIThread.Post`,并用代次号防止旧任务清掉新动画。

回归:`--proxydialog` 断言两个方向都出现中间态(展开 `214→373` 时 scale 采样
`0.83 / 0.95 / 1.00`,收起反向 `1.39 / 1.11 / 1.00`)、播完复位、内容恢复可见。

## 八、没做的

- **SOCKS5**:.NET 的 `IWebProxy` 只能表达 HTTP 代理(HTTPS 走 CONNECT)。
  要支持 SOCKS5 得引第三方(如 `HttpToSocks5Proxy`),AOT 下还要验裁剪 —— 暂不做。
  Clash/V2Ray 的混合端口通常同时接受 HTTP,填 HTTP 即可。
- **分域名规则 / bypass 列表**:手动模式所有请求一律走代理。
- **Android 的 ExoPlayer 数据源**:播放器内部用 OkHttp/HttpURLConnection,走系统代理;
  `ExoPlayerMediaPlayer._http`(下载用)已接,但播放流本身不受本设置影响。
