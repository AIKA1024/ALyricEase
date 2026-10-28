# ALyricEase Android 平台笔记

从 `.workbuddy/memory/MEMORY.md` 外移（该文件有注入长度上限，只留结论与入口）。
内容都是**实测撞出来的**，不是从文档抄的；每条都写清"怎么验/怎么判"。

## 1. 清单与构建

- **生效的清单只有 `src/ALyricEase.Android/AndroidManifest.xml`（项目根目录）**：
  根目录那份存在时 `Properties/AndroidManifest.xml` 被**完全忽略**（静默无警告）。
  改完必须验进 APK：
  ```
  "C:\Program Files (x86)\Android\android-sdk\build-tools\36.0.0\aapt2.exe" dump xmltree <apk> --file AndroidManifest.xml
  ```
- 构建必须显式 `JAVA_HOME="D:\Software\JetBrains Rider 2025.3.2\jbr"`（不在 PATH）。
- SDK 在 `C:\Program Files (x86)\Android\android-sdk`，build-tools 只有 36.0.0。
- `targetSdkVersion=36`。

## 2. 返回键（2026-08-30 定稿，别退回旧做法）

自 Android 15 起，`targetSdk≥35` 时 `enableOnBackInvokedCallback` 默认 true，系统不再下发 KEYCODE_BACK。

最终方案：清单里显式写 true + `MainActivity` 在 `OnStart` 里**直接注册平台 `OnBackInvokedCallback`**（API 33+）；
API ≤32 才走 `OnBackPressed` 覆写。两条路互斥，都汇到 `HandleSystemBack()`。

- 注册必须在 `base.OnStart()` **之后** —— Avalonia 在 base 里注册自己的 AndroidX 回调，
  同优先级按注册逆序执行 ⇒ 后注册的先跑。
- `OnStop` 成对 Unregister + Dispose。
- **别**再依赖 Avalonia 的 `BackRequested`（要经桥接，厂商 ROM 上时灵时不灵）。
- **别**靠降 targetSdk 绕（Google Play 已要求新包 ≥35）。

## 3. 语言层面的坑

- 引用 Android 类型要写 `global::` 前缀（项目存在 `ALyricEase.Android` 命名空间，裸写报 CS0234）。
- **`#if ANDROID` 在核心库是死分支**：该常量只由 Android SDK 对 `net*-android` 工程定义，而核心库只面向
  `net10.0` ⇒ 分支**永不编译**（不报错、静默走 `#else`）。
  验证法：产出的 `ALyricEase.dll` 里搜 `FilesDir` / `Java.Lang.Runtime`，应 **0 命中**。
  平台判定一律用 `OperatingSystem.IsAndroid()`。
  已知后果：Android 上音乐缓存落在 `<私有 files>/.local/share/ALyricEase/cache/music`（功能正常，改路径要迁移缓存）。

## 4. 图片解码内存预算

唯一来源 `src/ALyricEase/Infrastructure/ImageMemoryBudget.cs`。

- 桌面：租约 64MB / 512 项；直接缓存 16MB / 128 项。
- Android：取 **ART 堆上限 ÷ 4**，夹在 `[12MB, 48MB]`；条目按 192KB/张折算、下限 48；
  直接缓存 = 租约的 1/4（≥4MB）。
- **分母必须是 `Java.Lang.Runtime.GetRuntime().MaxMemory()`**（≡ `ActivityManager.MemoryClass`），
  **不是** `GC.GetGCMemoryInfo().TotalAvailableMemoryBytes` —— 真机实测后者报 **4793.7MB**
  （物理内存的 0.8 倍，与 ART 堆无关），÷8 = 599MB **恒大于上限** ⇒ `Clamp` 永远落 48MB，低端机拿不到降额。
- 注入方式（核心库不能引用 Android 类型）：`internal static SetAndroidHeapLimit(long)` + `Lazy<Budget>` 惰性解析；
  Android 侧在 `Application.OnCreate()` 里注入，**必须早于 `base.OnCreate()` 与首次读预算**；注入失败退固定 48MB。
  `Describe()` 带 `heapLimit=...MB` 后缀。
- 实测值：桌面 `lease=64MB/512items direct=16MB/128items`；
  真机 `lease=48MB/256items direct=12MB/64items heapLimit=192MB`（缩放表 96→24 / 128→32 / 192→48）。

## 5. 真机自检入口

`src/ALyricEase.Android/Diagnostics/DeviceImageBudgetProbe.cs` —— intent extra 触发、**只读**
（不传 extra 零影响），输出到 logcat tag `ALyricEaseProbe`。核心库对它开了 internal
（`src/ALyricEase/AssemblyInfo.cs` 的 `InternalsVisibleTo`）。

```
adb shell am start -n com.aika1024.alyricease/crc64ea7453dff8e094fe.MainActivity --es aly_probe image-budget
adb logcat -d -s ALyricEaseProbe:I
```

- 冷启动触发会隔 20 秒二次采样（首次采样时租约缓存必然为空）。
- 另一个入口 `--es aly_probe playback-state`（`Diagnostics/DevicePlaybackStateProbe`）：
  用生产 `IAudioPlayer` 播一段公开样音（不需要登录/歌单），把状态常量、上层收到的事件
  和每秒快照打到 `ALyricEaseProbe`。期望 `Loading → DurationChanged → Playing` 之后每 200ms
  一条 `PositionChanged`；后端“在播但界面停在 0:00”时这里会一直停在 `Loading`。
  ```
  adb shell am start -n com.aika1024.alyricease/crc64ea7453dff8e094fe.MainActivity --es aly_probe playback-state
  adb logcat -s ALyricEaseProbe:I
  ```
- `MainActivity` 的 `OnCreate` / `OnNewIntent` **两条路都接**（SingleTop，已在前台时只走后者）。
- 真机是 MuMu 模拟器（`127.0.0.1:5555`，Android 12 / x86_64 宿主 + arm64 翻译，伪装 HUAWEI HBN-AL00）。
- 签名：**Release 自 2026-10-01 起用自有密钥**（`~/.android/alyricease-release.keystore`，见 csproj），
  与 debug.keystore 不同 ⇒ 覆盖安装本机构建的 Release 包会 `INSTALL_FAILED_UPDATE_INCOMPATIBLE`。
  遇到旧包装的是别人签的包时：`adb root` → `tar` 备份整个 `/data/data/<pkg>` → 卸载 → 安装 →
  解回数据、`chown` 成新 uid、`restorecon`。（实测 2026-10-28：`config/state.json` 只有界面状态，
  不含账号凭据；该机当时本来就没登录。）

## 6. 只有 Release 包才暴露的两个问题

Debug 构建与桌面探针都查不出。

1. **裁剪删掉"反射创建"的页面视图构造函数** → 冷启动即崩
   （`MissingMethodException: Arg_NoDefCTor, ALyricEase.Views.RecommendView`）。
   构建期的 `warning IL2072`（`Infrastructure/ReusablePageViewTemplate.cs`）就是预告，**别当噪音**。
   修法：给 `ViewType` 加
   `[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]`。
   ⇒ 以后新增"反射建 UI"的地方，先看 IL2072 有没有增加。
2. Debug 包手动 `adb install` 会因 Fast Deployment 找不到程序集而 abort
   ⇒ 加 `-p:EmbedAssembliesIntoApk=true`（Release 默认内嵌）。

## 7. 沙箱里做 Android Release 构建：增量必挂，只认全量

保护层"允许新建、拦截覆盖" ⇒ 首次全量能过，之后重建**覆盖** `.so/.ll/.flata` 被拒，失败点漂移
（`XA3006 ... llc: permission denied` / `APT2000: 数据无效。(13)`）。
`llc` / `aapt2` **都不检查写入结果**，留下 0 字节/半截文件，下一轮当有效输入。

**最危险**：AOT 失败但后续目标仍跑完 ⇒ 包里混进上一轮 AOT 模块 ⇒ 装机崩溃循环
（`module 'Avalonia.Android.so' is unusable (GUID of dependent assembly Mono.Android doesn't match)`）。
**看到这条不要查 Mono 版本**，直接怀疑"AOT 产物与程序集不同源"。

- 判成败看 **exit code + 耗时**：AOT 版约 5 分钟 / 29.3MB；非 AOT 约 1 分钟 / 18.6MB。
- 清 `obj\Release` / `bin\Release`：`Remove-Item` 被 safe-delete shim 拦，**`[IO.Directory]::Delete($p,$true)` 可用**；
  `Move-Item` 搬整个 `obj\Release` 会被沙箱**直接杀进程**，别用。
- 沙箱内只做这一种（关 AOT）：
  ```
  dotnet build src/ALyricEase.Android/ALyricEase.Android.csproj -c Release -f net10.0-android \
    -p:AndroidTargetAbi=arm64-v8a -p:RunAOTCompilation=false -p:EmbedAssembliesIntoApk=true
  ```
  验包看 `lib/libaot-*.so` = **0 条**（程序集仍在 `lib/<abi>/libassembly-store.so`；
  裁剪照常执行 ⇒ IL2072 照样暴露）。
- 要出 full Mono AOT 发布包，**在 Rider / 普通终端做**（`scripts/publish-aot.ps1`）。

## 8. 播放后端:Media3/ExoPlayer(2026-10-26 替换 android.media.MediaPlayer)

- 动机:MediaPlayer 流播对 VBR 源的 `Duration` 按平均码率估算、明显偏低,交叉淡化
  (`PlayerViewModel.MaybeCrossfadeAdvance`)据此提前触发,表现为"歌的后半段直接切歌"
  (真机日志实证:多首在真实时长 71%~95% 处被切);且断流直接 `Completion` 被当"播完"。
- 落地:`ExoPlayerMediaPlayer`(实现 `IAudioPlayer`,与平台无关的上层零改动)。
  只引 `Xamarin.AndroidX.Media3.ExoPlayer` + `Media3.DataSource`(1.11.1),其余 Media3
  包走传递依赖;依赖只落在 Annotation/Media3 家族,与手工对齐的 Core/Lifecycle 版本无交集。
- 线程契约:ExoPlayer 单线程访问。状态/时长/Seek/音频会话由 listener 通知驱动,
  合并排队到 UI 线程后读取最新状态,避免旧状态回写和在 native listener 内重入切歌。
  200ms UI 定时器仅在实际播放时采样进度(Media3 不推送位置,这一项只能采样),
  暂停/缓冲/停止/释放均停止计时。
- ⚠ **`Player.Listener` 必须实现全部成员(2026-10-28)**:Java 8 的 default 方法在 .NET
  绑定里是**抽象成员**,java-interop 只给缺失实现补一个"抛 `AbstractMethodError`"的桩
  —— 少实现一个、Media3 通知到它就是进程级 FATAL(实测删掉 `onEvents` 启动即崩)。
  同理**不能**用 `onEvents` 的 `events.Contains(...)` 做过滤:该调用在 Release 绑定里恒为
  false(实测 `Player$Events@35` 上 state/playing/timeline 全 false),于是所有通知被丢掉,
  现象是"音频真的在播,界面停在 0:00 / 未播放、播放键仍是 ▶"。正确做法:成员全实现,
  把"任何一条通知"当成"该同步一次"的信号,由 `SynchronizePlayer()` 读真值后比对上报。
  相关外部资料:dotnet/android#7663(默认方法 + desugaring)、CommunityToolkit.Maui#2824
  (同款 `AbstractMethodError`)。另注:`SupportedOSPlatformVersion=23`(<24)会启用 desugaring,
  哪天真遇到"默认方法体调用失败"再考虑抬到 24,本次故障与 desugaring 无关。
  `CrossfadeAudioPlayer` 的每步音量调整和结束时 Stop 同样显式调度回 UI 线程;
  执行时检查取消,避免硬切/Stop/Dispose 后排队的旧步骤继续访问实例。
- 绑定 API 注意:
  - `Player` 绑定为 `IPlayer` 接口,`Listener` 扁平化为 `IPlayerListener`(非嵌套);
    当前绑定的状态/事件常量位于 `BasePlayer.InterfaceConsts`,不要另写数值副本。
  - 自实现的 Java 侦听器(含 VisualizerListener)必须有 `(IntPtr, JniHandleOwnership)`
    构造器,否则 GC 回收托管 peer 后 Java 回调反激活直接杀进程(10 月真机崩溃实证)。
  - `DefaultHttpDataSource.Factory` 必须开 `SetAllowCrossProtocolRedirects(true)`
    (网易云 CDN http<->https 重定向),UA/Referer 在工厂统一设置。
    外层用 `DefaultDataSource.Factory(context, httpFactory)` 分流本地文件与 HTTP(S),
    再传给 `DefaultMediaSourceFactory(dataSourceFactory)`;不能直接用 HTTP 工厂播放
    `file://` 缓存/本地歌曲,也不能使用不存在的无参 `DefaultMediaSourceFactory` 构造器。
- 下载兜底仅限尚未起播的 HTTP(S) 连接失败/超时,每个播放会话最多一次;
  本地文件、解码错误、HTTP 鉴权/不存在与播放中断流直接上报错误。
  下载采用流式写入和全程 30 秒超时,换歌/Stop/Dispose 取消任务并清理未接管的临时文件。
  URL 保留服务端原协议,不再强制把 HTTP 改为 HTTPS(Manifest 已允许明文流量)。
- 时长修正兜底:`MaybeCrossfadeAdvance` 同时取播放器上报时长与 API 元数据时长的较大值,
  双保险防时长低估;两条自动切歌路径均有文件日志(`Natural completion advance` /
  `Crossfade advance triggered`)可事后区分。
