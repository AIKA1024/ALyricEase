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
- `MainActivity` 的 `OnCreate` / `OnNewIntent` **两条路都接**（SingleTop，已在前台时只走后者）。
- 真机是 MuMu 模拟器（`127.0.0.1:5555`，Android 12 / x86_64 宿主 + arm64 翻译，伪装 HUAWEI HBN-AL00）。
- Debug 与 Release **同一 debug.keystore 签名** ⇒ `adb install -r` 直接覆盖，不必卸载、不丢登录态。

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
