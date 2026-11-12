# ALyricEase Android 平台笔记

从 `.workbuddy/memory/MEMORY.md` 外移（该文件有注入长度上限，只留结论与入口）。
内容都是**实测撞出来的**，不是从文档抄的；每条都写清"怎么验/怎么判"。

## 1. 清单与构建

- **生效的清单只有 `src/ALyricEase.Android/AndroidManifest.xml`（项目根目录）**：
  根目录那份存在时 `Properties/AndroidManifest.xml` 被**完全忽略**（静默无警告）。
  改完必须验进 APK：
  ```
  "C:\Program Files (x86)\Android\android-sdk\build-tools\37.0.0\aapt2.exe" dump xmltree <apk> --file AndroidManifest.xml
  ```
- 构建必须显式 `JAVA_HOME="D:\Software\JetBrains Rider 2025.3.2\jbr"`（不在 PATH）。
- SDK 在 `C:\Program Files (x86)\Android\android-sdk`，platform 有 android-35/36/37.0，build-tools 有 36.0.0/37.0.0。
  装 API 37 那步要管理员权限（`platforms` 目录 Users 只读）：`InstallAndroidDependencies` 普通权限跑会报
  `XAIAD7019 UnauthorizedAccessException`，提权后才能落盘。
- `targetSdkVersion=37`、`minSdkVersion=24`（.NET 11 的 Android workload 不再支持 API 23）。
- **运行时只剩 CoreCLR**（2026-11-11 实测，别再找 Mono 路子）：
  - `UseMonoRuntime=true` ⇒ `error NETSDK1242: .NET 11.0 及更高版本不支持使用 Mono 运行时生成 Android 项目`。
  - `RunAOTCompilation=true` 单独用 ⇒ `error XA1044: 与 CoreCLR 运行时不兼容`（是 error，不是警告，构建直接停）。
  - ⇒ **"full Mono AOT 发布"不存在了**，`lib/libaot-*.so` 再也不会产生。APK 里看到的是
    `libcoreclr.so`(4.8MB) + `libclrjit.so`(2.8MB) + `libassembly-store.so`(18MB，程序集全塞在这里面，没有独立 `.dll`)。
  - Release 默认 `PublishReadyToRun=true` + `PublishReadyToRunComposite=true` + 打包 PGO profile
    （`Microsoft.Android.Sdk.CoreCLR.targets` 里定的；`PublishTrimmed` 不为 true 时 R2R 会被自动关掉）。
    语义是 **IL 预编译成本机码但保留 IL**，JIT 仍可回退/分层编译 —— 比 Mono AOT"软"，但比纯 JIT 快。
    Debug 是真 JIT。
  - 体积对照（arm64 已签名）：.NET 10 Mono AOT **34MB** → .NET 11 CoreCLR+R2R **33MB**，基本持平。
  - NativeAOT 仍在（`PublishAot=true`），但官方标注 "Experimental on Android"，且与 Avalonia.Android 已知冲突
    （见 `scripts/publish-aot.ps1` 头部，UnsatisfiedLinkError at Application.OnCreate）。

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
  `net11.0` ⇒ 分支**永不编译**（不报错、静默走 `#else`）。
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
- 第三个入口 `--es aly_probe scroll`（`Diagnostics/DeviceScrollProbe`）：在**当前页面就地**跑
  滚动 A/B 与 jitter 判别（不导航，先手动进到有长列表的页面），结论见 §12。
  ```
  adb shell am start -n com.aika1024.alyricease/crc64ea7453dff8e094fe.MainActivity --es aly_probe scroll
  adb logcat -d -s ALyricEaseProbe:I
  ```
- activity 全名若又变了，用 `adb shell cmd package resolve-activity --brief com.aika1024.alyricease` 取。
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

> ⚠️ **下面这段 AOT 崩溃形态在 .NET 11 已经不可能复现**：Mono 运行时被 SDK 移除（`NETSDK1242`），
> 没有 Mono AOT 就没有"上一轮 AOT 模块混进本轮包"这回事。留着是为了将来翻旧账时知道当年怎么挂的。

**最危险（仅 net10.0 及以前的 Mono AOT 时期）**：AOT 失败但后续目标仍跑完 ⇒ 包里混进上一轮 AOT 模块 ⇒ 装机崩溃循环
（`module 'Avalonia.Android.so' is unusable (GUID of dependent assembly Mono.Android doesn't match)`）。
**看到这条不要查 Mono 版本**，直接怀疑"AOT 产物与程序集不同源"。

- 判成败看 **exit code + 耗时**：.NET 11 CoreCLR Release 单 ABI 约 4.4 分钟（含 R2R）；
  .NET 10 时代 Mono AOT 约 5 分钟 / 29.3MB，非 AOT 约 1 分钟 / 18.6MB。
- 清 `obj\Release` / `bin\Release`：`Remove-Item` 被 safe-delete shim 拦，**`[IO.Directory]::Delete($p,$true)` 可用**；
  `Move-Item` 搬整个 `obj\Release` 会被沙箱**直接杀进程**，别用。
- 沙箱内做 Release 构建（**别再传 `RunAOTCompilation=false`**，CoreCLR 下那会刷 XA1044 警告）：
  ```
  dotnet build src/ALyricEase.Android/ALyricEase.Android.csproj -c Release -f net11.0-android \
    -p:AndroidTargetAbi=arm64-v8a -p:EmbedAssembliesIntoApk=true
  ```
  验包看 `lib/<abi>/` 是 `libcoreclr.so` + `libclrjit.so` + `libassembly-store.so`，
  **没有** `libmonosgen-2.0.so`；裁剪照常执行 ⇒ IL2072 照样暴露。
- 要出发布包，**在 Rider / 普通终端做**（`scripts/publish-aot.ps1`）。

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

## 9. 应用图标(mipmap + 自适应图标,2026-10-28)

**症状**:桌面上是 Avalonia 模板自带的徽标(蓝"A"白底),不是项目图标。

**缘由**:模板只在项目根放了一张 `Icon.png`,清单写 `android:icon="@drawable/Icon"` ——
单张 drawable 会被系统按 48dp 缩放,既糊又躲不开启动器掩码;API 26+ 更不会走自适应图标,
边框/圆角全靠启动器自己猜。

**现在的组成**(全部由 `src/ALyricEase/Assets/icon_256.png` 生成):

| 资源 | 用途 |
|---|---|
| `Resources/mipmap-<bucket>/ic_launcher.png` | 传统图标,5 个密度(48/72/96/144/192),内容占 ~92% |
| `Resources/mipmap-<bucket>/ic_launcher_round.png` | 圆形变体(图标本身是圆盘,同一张) |
| `Resources/mipmap-<bucket>/ic_launcher_foreground.png` | 自适应图标前景:108dp 画布,内容 **66dp** |
| `Resources/mipmap-anydpi-v26/ic_launcher{,_round}.xml` | 自适应图标(API 26+),背景取 `@color/ic_launcher_background` |
| `Resources/values/colors.xml` | `ic_launcher_background` = 白;透明底在部分启动器上会变黑 |

清单:`android:icon="@mipmap/ic_launcher"` + `android:roundIcon="@mipmap/ic_launcher_round"`。
模板的 `Icon.png`(`drawable/Icon`,Avalonia 徽标)已删除,csproj 里那条 `AndroidResource` 也一并去掉 ——
否则它还会被当成"应用图标"的旧引用留在包里。

**换图标时**:替换 `src/ALyricEase/Assets/icon_256.png` 后跑
```
python scripts/make-android-icons.py
```
纯标准库实现(自己解/写 PNG,不依赖 Pillow),尺寸表写在脚本里。不用手改密度。

**为什么前景只占 66dp**:自适应图标的 108dp 画布只有中心 **72dp** 会显示,且启动器还要套
自己的圆形/水滴形掩码。内容铺满 108dp 会被裁掉一圈;66dp 留 3dp 安全边,圆盘完整可见。
背景层负责填满被裁掉的区域,所以留白由 `ic_launcher_background` 决定,不是"图标变小了"。

**验证**:
```
aapt2 dump badging <apk> | grep application-icon
# application-icon-*: 'res/mipmap-anydpi-v26/ic_launcher.xml'  ← API 26+ 走自适应
aapt2 dump xmltree --file AndroidManifest.xml <apk> | grep -i icon
# android:icon=@0x7f0c0000  android:roundIcon=@0x7f0c0002
```
桌面图标与 Android 12+ 启动页用的都是这套资源(启动页取应用图标,不需要另配
`windowSplashScreenAnimatedIcon`)。

## 十、构建卡死/失败的恢复（2026-10-28 实测）

`publish-aot.ps1` 跑双 ABI 时，第二个 ABI（armeabi-v7a）可能在
`[112/113] System.Private.CoreLib.dll` 卡死：java 进程 CPU 零增长、`obj` 目录几分钟没有新文件。
判断"死"而不是"慢"就看这两条，别凭时长猜。

杀进程后**必须先整目录清**再重试，只删子目录会让增量状态更不一致（只清这两个目录即可，
不用清整个 obj，还原很慢）：

```
src\ALyricEase.Android\obj\Release\net11.0-android
src\ALyricEase.Android\bin\Release\net11.0-android
```

不清理会依次撞上这些误导性错误：

| 症状 | 真实原因 |
|---|---|
| `XAGNM7009 missing native code generation state for architecture 'Arm'` | 上一次中断留下的半套 AOT 状态 |
| D8: `JavaProxyThrowable is defined multiple times`（`classes.zip` vs `mono.android.jar`） | 同上，`classes.zip` 是脏的 |
| `XA3006 typemaps.armeabi-v7a.ll: Could not open input file` | 只删了 `obj/.../android` 子目录，生成该文件的那一步被增量跳过 |

清完重跑约 3.5 分钟一次过。

**另外**：直接 `dotnet publish` 不走脚本时，产物 APK 名是 `com.aika1024.alyricease-Signed.apk`，
脚本里的 ABI 校验与改名不会执行，需要手工改名成 `ALyricEase-<abi>-Signed.apk` 再上传。

## 10. 装不上先看 ABI:bin 下的 Signed.apk 是"最后一次构建的 ABI"(2026-10-31)

**症状**:安卓 16(64 位-only)上"无法安装",没有别的线索。

**缘由**:`scripts/publish-aot.ps1` 按 ABI 分别构建,产物在
`artifacts/coreclr-android-per-abi/<abi>/`(文件名带 ABI);但**最后一次构建**同样会在
`src/ALyricEase.Android/bin/Release/net11.0-android/` 留下同名
`com.aika1024.alyricease-Signed.apk` —— 那是最后跑的那个 ABI(2026-10-28 那次是
armeabi-v7a)。把这份发给 64 位-only 设备就是 `INSTALL_FAILED_NO_MATCHING_ABIS`
(系统提示"应用与设备不兼容")。

**判据**(一条命令):
```
aapt2 dump badging <apk> | grep native-code
# native-code: 'arm64-v8a' 'armeabi-v7a'   ← 通用包,两种都装得上
# native-code: 'armeabi-v7a'               ← 只有 32 位,安卓 16 必然装不上
```

**怎么出包**:
- 发布用单 ABI:走发布脚本,只发 `artifacts/coreclr-android-per-abi/<abi>/ALyricEase-<abi>-Signed.apk`
  (别拿 bin 里的那份,它没有 ABI 标识,还会被下一次构建覆盖);
- 通用包(arm64 + v7a):`dotnet build src/ALyricEase.Android/ALyricEase.Android.csproj -c Release
  -f net11.0-android` —— 不传 `AndroidTargetAbi` 时 csproj 走
  `RuntimeIdentifiers=android-arm;android-arm64`,产物含两套 `.so`。

**顺带核过(2026-10-31)**:arm64 包的 `.so` 都是 `p_align=0x4000`(16KB 页),
`zipalign -c -P 16 4` 通过 ⇒ 安卓 15/16 的 16KB 页要求不是拦路虎;targetSdk 37、minSdk 24
也都合规。另外仓库只出 ARM 包:x86_64 模拟器(没有 ARM 翻译)装不了,要另加 `android-x64`。

## 11. 启动页图标为什么有锯齿,以及怎么修(2026-11-02)

**现象**:Android 12+ 冷启动时,屏幕中央的图标边缘是硬阶跃(1px 台阶),放大看像马赛克;
桌面上的启动器图标却是正常的。

**根因不在缩放算法,在"喂进去的分辨率"**:

- Android 12+ 的启动页把图标画在 **288dp** 的框里(不设 `windowSplashScreenIconBackgroundColor`
  时按 288dp 画;设了图标底板才是 240dp)。
- 应用图标是自适应图标(adaptive-icon),它的**前景层是 108dp 画布** ⇒ 系统把 108dp 拉到 288dp,
  即 **2.67 倍**。图标内容按 66/108 摆放,于是最终显示为 66/108×288 = **176dp**。
- 而密度桶最高只到 xxxhdpi(108dp→432px),**任何 mipmap 都喂不满 288dp**:
  560dpi 的手机上 176dp = 616px,对应前景 PNG 里的内容只有 264px ⇒ 放大 2.33 倍。
  实测(1440×3120 截图)图标画幅 615px,和这个推算完全对上。

**判据**(不用猜):截图里量图标画幅,`画幅 / 176` 就是设备密度;
再和 `Resources/mipmap-xxxhdpi/ic_launcher_foreground.png` 的内容像素(264)比,
倍数 >1 就是在放大。另:桌面上正常、只有启动页糊 ⇒ 就是这条,不是图标本身的问题。

**修法**:给启动页单独一张按目标尺寸预渲染的图,让系统只**缩小**:

| 文件 | 内容 |
|---|---|
| `Resources/drawable-nodpi/splash_icon.png` | 1152px 画布(288dp@640dpi),内容 704px,66/108 比例不变 |
| `Resources/values-v31/styles.xml` | `android:windowSplashScreenAnimatedIcon=@drawable/splash_icon` |

- 为什么是 1152:**nodpi 不随密度缩放**,按像素画;取 288dp 的最高密度档(640dpi)作画布,
  所有设备都只会缩小(缩小永远平滑)。560dpi 上 1152→1008(0.875x),480dpi 上 1152→864(0.75x)。
- 为什么内容还是 704 而不是铺满:要和自适应图标**同一个视觉尺寸**。实测同机 A/B:
  改前/改后画幅都是 529×518、中心都在 (540,953) —— 尺寸位置一字不差,只有边缘采样变干净。
- 生成用 Lanczos3(按 alpha 预乘)。`resize_area` 放大时会退化成"复制整像素",
  源图的 1px 硬阶跃原样保留,台阶就是这么来的;脚本里两个函数各管放大/缩小。
- 生成量:`python scripts/make-android-icons.py`(纯标准库,约 2 秒)。

**验证**:
```
aapt2 dump resources <apk> | grep -A6 "style/MyTheme.NoActionBar"
#   (v31) (style) size=3 …
#     0x0101062d=@drawable/splash_icon      ← 0x0101062d = windowSplashScreenAnimatedIcon
unzip -l <apk> | grep splash_icon           # res/drawable-nodpi-v4/splash_icon.png
```

**遗留**:源图 `src/ALyricEase/Assets/icon_256.png` 只有 256px,放到 616px 显示时是"软"的
(但不再有台阶)。要真正锐利得换更大的原图(≥1024)或改矢量;换图后重跑脚本即可。

## 12. 滚动性能:CacheLength 是负收益,瓶颈在容器实化(2026-11-12 真机实测)

背景:用户报安卓滚动卡,怀疑 `CacheLength`(虚拟化缓冲)能救。实测结论是**反的** ——
调大更卡。判别方法:`DeviceScrollProbe` 在一加 7 Pro(GM1911 / 1440×3120 @560dpi / arm64)
的「我喜欢的音乐」(1104 首)上,用固定 14px/帧的 rAF 脚本复现滚动,每轮同起点同距离。

```
baseline   61.9fps  p95=16.6  max=17.0   >50ms=0                空转 rAF
jitter     59.0fps  p95=17.1  max=67.3   >50ms=1/336            同区域往返,不新建容器
CL=0       54.7 / 55.9fps  max 70.7 / 83.7   >50ms=7 / 7   分配 61.6 / 59.3MB
CL=0.5     52.3 / 52.2fps  max 168 / 139     >50ms=11 / 11 分配 57.9 / 57.5MB
```

**三条结论**:

1. **`CacheLength=0.5` 是负收益**:帧率 −3.1fps、>50ms 掉帧从 7 涨到 11、max 从 70 涨到 168。
   分配确实降了 4MB/轮(回收变少是真的),但每帧要 Arrange 的元素翻倍,代价更大。
   ⇒ **歌曲列表保持默认 0**,别照抄 `ArtistView`/`BrowseView` 的 0.5(那是横向卡片区,行数少)。
2. **jitter 59.0 vs baseline 61.9**:稳态 Arrange(视口 13 行)只值 2.9fps(~5%),可接受。
3. **滚动 55.3 vs jitter 59.0**:再掉 3.7fps,且 >50ms 从 1 涨到 7 ——
   **毛刺主要来自"容器实化/回收"这一刻的一次性成本**,不是每帧布局。

**尖峰不是 GC**:同轮 logcat 里 ART 的并发复制 GC `paused` 只有 28~70µs
(total 8~26ms 走后台线程),与 67~168ms 的尖峰对不上。也不是封面解码(四轮都从 y=0 起滚,
第 2~4 轮命中缓存,尖峰没有下降)。

**机制**:Avalonia 的滚动是**布局驱动**的 —— `ScrollContentPresenter.OnPropertyChanged(Offset)`
直接 `InvalidateArrange()`,下一帧 `ArrangeOverrideImpl(size, -Offset)` 重摆子元素,不走合成层平移。
所以帧成本 ∝ 每帧参与 Arrange 的元素数;一行 `TrackRow` 约 40 个 visual,实化一行就是
40 次样式匹配 + 绑定求值 + 5 个 TextBlock 的 TextLayout。

**已做:触屏精简模板(2026-11-12,同机同页复跑)**

三套 `*TouchTheme`(`TrackRowSearchTouchTheme` / `TrackRowPlaylistWideTouchTheme` /
`TrackRowAlbumTouchTheme`)去掉触屏下恒隐的播放·更多·红心·音源角标、两个 hover 层,
标题也不再需要 ColumnSpan 让位(触屏没有 hover)。

接入的两个坑:
- **样式必须比原规则多一个 `.touch` 类**。页面级规则特异性更高(歌单页 `.narrow ItemsControl.tracks
  views|TrackRow` 是 2 类 2 型),单在 `TrackRow.axaml` 加 `views|TrackRow.touch` 覆盖不到;
  `PlaylistView` / `RecentPlaybackView` / `SearchView` 各补一条 `.touch` 变体。
- **专辑页原来是 DataTemplate 内联 `Theme=`**,那是 LocalValue,样式改不动 ⇒ 已改成
  `Classes="album-row"` + 页面样式两条。

| 指标 | 原模板 | 精简模板 |
|---|---|---|
| CacheLength=0 帧率 | 55.3 | **58.3**(+3.0) |
| >50ms 掉帧 | 7 / 7 | **1 / 4** |
| 最长帧 | 70.7 / 83.7ms | **50.2 / 50.5ms** |
| 分配/轮 | 61.6 / 59.3MB | **27.7 / 25.8MB**(−57%) |
| jitter(不建容器) | 59.0 | 59.9 |
| baseline(空转) | 61.9 | 61.2 |

滚动已到 baseline 的 95%(58.3 / 61.2),容器实化的代价从 3.7fps 降到 1.6fps。
分配腰斩是最硬的证据:那一半 Button/Border 确实在被逐行创建。
`CacheLength=0.5` 在新模板下依然更差(55.9 vs 58.3),**结论不变,别加**。

⚠ 精简模板**不复用** `row-title` / `row-duration` / `row-ops` / `row-heart` 这些类,
它们绑着"桌面 hover 让位"的样式(标题 ColumnSpan 2↔1、时长 IsVisible 切换),放进去会串味;
歌名只用 `fileMissing`(删除线)一个类。

**附带修正**:精简模板里没有播放/喜欢按钮(整行单击播放)⇒ 设置页「歌曲行的播放与喜欢按钮互换位置」
在触屏下改了没有任何效果,已按 `InteractionDefaults.IsTouchPrimary` 隐藏
(`SettingsViewModel.RowHoverActionsSupported`,不用平台 API,与行内实现同源)。

**试过但无效的(2026-11-12,同一台机同一探针,别重复踩)**

- **`AndroidPlatformOptions.RenderingMode = [Vulkan]` → 更差**。58.3 → 51.5fps,
  最长帧 50 → 200ms(一加 7 Pro / 骁龙 855 / Adreno 640)。Vulkan 驱动的管线/shader
  创建开销压过了提交开销的收益。⇒ 留默认 Egl。`Application.CustomizeAppBuilder` 里有注释留存。
- **`IRecyclingDataTemplate`(容器内容回收)** → 无差异。58.3 → 58.6fps(噪声内),
  分配只降 4%。它绕开的是 #20259 说的"每次重建模板内容",但**重建本来不是大头**:
  每轮滚 78 行,一行 15 个 visual ≈ 5KB,合计 ~400KB,只占 26MB/轮的 1.5%。
  大头是每帧布局的临时分配(≈78KB/帧),而 Gen0 GC 很快,不构成卡顿。⇒ 已撤销。
- **`CacheLength`**(官方文档把它叫 `BufferFactor`,12.1.6 里只有 `CacheLength`)→ 负收益,见上。
- **`ItemsRepeater`** → 12.0 已退役(官方 performance 文档写明),社区反馈比 VSP 更慢(FluentAvalonia #445),
  不用考虑。
- **`SkiaOptions.MaxGpuResourceSizeBytes`**(默认 28MB)→ 未试:瓶颈在 UI 线程布局,
  不在 GPU 纹理回收,预期无效。

**天花板**:精简模板下滚动 58.3 / 空转 61.8 = **94%**。剩下的 ~3.5fps 是 Avalonia 布局引擎的固定成本
(每帧 `Offset` ⇒ 整屏 Arrange),不换架构拿不回来。再往下只能砍功能(去封面、合并文本行),
或者等上游 #20259 那个"内容级虚拟化"落地。

**复跑**:
```
adb shell am start -n com.aika1024.alyricease/crc64ea7453dff8e094fe.MainActivity --es aly_probe scroll
adb logcat -d -s ALyricEaseProbe:I
```
⚠ 必须用 **Release** 包测(Debug 是 JIT,帧率没有参考价值),先手动进歌单页再发 intent。
横屏(TrackRowPlaylistWideTouchTheme)与专辑页(TrackRowAlbumTouchTheme)已用截图确认
列顺序/对齐无回归。

## 13. 悬浮歌词去掉按钮行，改成纯歌词窗口(2026-11-12)

**症状**：手机上桌面歌词拖到最上面，也总比屏幕顶差一行，看着像"移不高"。

**根因**：`AndroidDesktopLyricService.CreateWindow()` 的窗口根是垂直 `LinearLayout`，
**第一个子 View 是 48dp 工具栏**（A− / A+ / 译 / 背景 / 穿透 / ×），歌词在它下面；
`ClampPosition()` 把 `_layout.Y` 钳到 `[0, …]`，而窗口 0 点是**工具栏的顶部**，不是歌词的顶部。
唯一例外是开穿透（工具栏 `Gone` + `Y` 补偿 48dp），但那时窗口不能拖也不能点。

**做法**：整行删掉，窗口只装歌词本体。六项功能在设置页「桌面歌词」弹窗里全都有
（总开关 / 显示翻译 / 暂停隐藏 / 穿透 / 字号 / 背景不透明度），悬浮条只是快捷入口。

- 删 `_toolbar` / `_translationButton` / `_collapsedToolbarHeight` 三个字段，
  以及 `Render()` 里那段 `heightDelta` 的 `Y` 补偿与 `Visibility` / `Checked` 同步；
- `ClampPosition()` 去掉 `expectedHeight` 参数（原先靠它预估工具栏收起后的高度），
  窗口高度即歌词高度，`Y` 下限 0 就能贴顶；底部仍留 48dp 避开手势条；
- 左右 padding 改为对称 12dp（原来右侧只有 4dp，是为了贴 × 按钮）；
- `Hint` 文案改为「拖动可调整位置，字号/翻译/背景/穿透在应用设置里调整」。

⚠ **连带约束**：穿透开启后窗口不接收触摸，而 Android 没有桌面那套全局快捷键，
唯一出口是设置页歌词弹窗里的 `ClickThrough` 开关 ⇒ 那个开关不能按平台隐藏，
`IsEnabled` 也必须继续绑 `Enabled`。

窗口最小高度仍有保障：`RefreshText()` 保证 `Original` 永不为空（无歌时是 `ALyricEase`，
无词时是歌名），一行文字 + 16dp 内边距足够拖动。

**真机验证**（一加 7 Pro，1440×3120）：`dumpsys window` 读到的悬浮窗 `frame` 顶边 = 128px，
正好等于状态栏高度 ⇒ `Y=0` 已贴到系统允许的最上沿，歌词到状态栏只差 8dp 内边距；
窗口高 253px（≈72dp，两行 24sp）。拖到中部再拖回，`frame` 正确回到 128。
设置弹窗里总开关能关能开：关闭后卡片显示「已关闭」、悬浮窗消失，再开时原地重建。

⚠ 贴顶时窗口会盖住应用自己的顶部按钮（汉堡菜单等）。这是悬浮层的固有行为（用户拖开即可），
不是本次改动的回归 —— 原先带 48dp 工具栏时盖得更多。

⚠ `adb shell input tap` 点 Avalonia `ToggleSwitch` 要落在轨道中心，点边缘可能不触发
（本次排查中踩过一次，误判成"关不掉"）。

### 13.1 左右拖不动 = 窗口宽度被写死成屏宽

**症状**：歌词只能水平居中，左右几乎拖不动。

**根因**：`ClampPosition()` 里有一句
`_layout.Width = Math.Min(Dp(480), Math.Max(Dp(120), WidthPixels - Dp(16)))`。
1440px / 3.5 密度下算出来 1384px ⇒ X 的活动范围只剩 `1440-1384 = 56px`（16dp），
两个端点看起来就是"左右各差一点点"，等同居中。

**修法**：窗口宽度按文字实测宽度算，用**显式像素值**（不是 `WrapContent`）。

- `FitWindowWidth()`：`width = max(原文宽, 翻译宽) + 左右 padding(24dp)`，
  再 `Clamp(…, 120dp, min(480dp, 屏宽))` 写进 `_layout.Width`；`Render()` 每次更新文字后调用；
- 文字宽度用 `TextView.Paint.MeasureText` 量（与渲染同一份字体/字号，不会对不上）；
- `text` / `_original` / `_translation` 的 `LayoutParams` 用 `MatchParent`：文字永远填满窗口内容区，
  居中交给 `TextView.Gravity = Center`；
- `ClampPosition()` 只钳位置，宽度读 `_layout.Width`（`>0` 才用）；
- 保留 `_root.LayoutChange`：换行导致高度变化时重钳 `Y`；X/Y 没变就不 `UpdateViewLayout`，防死循环。

⚠ **为什么不用 `WrapContent`**（第一版就是，已回退）：实测一加 7 Pro 会把 `WrapContent` 的
悬浮窗**钳到 320dp**（`frame=[0,260][1120,643]`）。13~14 字的日文歌词（14×24sp≈1176px）因此
被挤成两行，用户看到的就是"明明屏幕一行放得下却换行"。显式像素宽度不受这个限制。

**真机结果**：窗口从 1384px 降到 580~1131px（随文字变化），X 活动范围 0~859px（245dp），
左右拖动恢复正常。宽度与公式精确吻合：11 字 → 1008px、7 字 → 668px（= 字数×84 + 84 padding）。

### 13.2 翻译不居中（同日修）

两行字号不同（翻译 = 原文 × 0.7），宽度都是填满窗口内容区，居中靠 `TextView.Gravity = Center`。
⚠ 如果改回 `WrapContent`，较窄的那行会被父 `LinearLayout` 的默认 `Gravity=Left` 顶到左边
（第一版就是这样）。文字容器上的 `text.SetGravity(CenterHorizontal)` 作为兜底一并保留。

真机实测两行中心 117/117，完全对齐。

**关于"一行放得下却换行"**：第一版当成"宽度上限多扣了 16dp"，那只占 56px；真正的限制是上面
那条 320dp 的 `WrapContent` 钳制。改成显式宽度后 14 字句能拿到 1176px，不再换行。

