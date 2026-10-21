# 应用自更新(Velopack + GitHub Releases)

> 2026-10-01 接入。客户端库 `Velopack`(NuGet,当前 1.2.161),更新源 = GitHub Releases
> (`https://github.com/AIKA1024/ALyricEase`)。**全量 + 增量(delta)更新**都由 vpk/Velopack
> 原生支持:发布时同时产出 full 与 delta 包,客户端检查到新版本后优先下 delta 重建,
> 失败自动回退全量包(`DownloadUpdatesAsync` 内置该语义,代码不用管)。
>
> Android 不走 Velopack(它没有 Android 运行形态),自实现一条链路,见 §四。

## 一、代码侧(已接好的部分)

| 位置 | 职责 |
|---|---|
| `src/ALyricEase/Services/Update/IAppUpdateService.cs` | 能力抽象 + `AppUpdateInfo` + `NullAppUpdateService`(Android/无头占位) |
| `src/ALyricEase.Windows/Services/VelopackUpdateService.cs` | Velopack 实现:检查/下载(进度 0-100)/`ApplyUpdatesAndRestart`;另拉 GitHub API 的 release body 当更新说明(tag = 版本号) |
| `src/ALyricEase.Windows/Program.cs` | `Main` 最前面 `VelopackApp.Build().Run()`(**必须在一切逻辑之前**,处理 `--squirrel-*` 安装回调);DI 注册 |
| `src/ALyricEase/ViewModels/SettingsViewModel.cs` | 设置页状态机:`CheckForUpdatesCommand`(点按钮才联网查)→ 有更新填弹窗数据;`ConfirmUpdateCommand` 下载;`RestartToUpdateCommand` 重启 |
| `src/ALyricEase/Views/UpdateDialogView.axaml` | 模态弹窗三态:确认(版本+更新说明+取消/更新)→ 下载(确定进度条,不可关)→ 待重启(立即重启/稍后);挂在两个宿主壳层最末,ZIndex 200 |
| `src/ALyricEase/Views/SettingsView.axaml` | 底部「详细信息」Expander:应用名/版本+检查更新按钮/安装日期;`UpdateSupported=false`(开发目录直跑、Android)时按钮隐藏 |

行为要点:

- **只在用户点"检查更新"时联网**,启动不做自动检查。
- ⚠ **必须显式 `new GithubSource(repoUrl, null, false)`**(2026-10-01 实测 404 案):
  `UpdateManager(string)` 对 http(s) URL 一律构造 `SimpleWebSource`,**不会**识别 github.com
  仓库地址 —— 结果是去抓 `github.com/{repo}/releases.win.json`,GitHub 返回 404。
  `vpk download github` 走显式 GithubSource 所以验证时发现不了这个坑。
- ⚠ **同版本重打**:vpk pack 拒绝在本地 `Releases/` 已有同/更高版本时打包(FTL),
  先删本地 `Releases/` 再 pack,然后 `gh release upload --clobber` 原地替换资产。
- 无更新 → 设置页 `Status` 提示"当前已是最新版本";检查/下载失败也落 `Status`。
- 下载完成不自动重启(正在播放会被打断),弹窗切"立即重启"态,用户自己选时机。
- 重启前 `_state.Flush()` 落盘偏好(更新器会强杀进程,窗口关闭路径的 Flush 未必执行)。
- 版本号展示优先 Velopack 记录的安装版本;未打包回退程序集 `InformationalVersion`。
- 安装日期 = 当前版本目录 exe 的创建时间(vpk 安装/更新时整目录写入)。

## 二、发布流程(发布新版本时做)

1. 正常发布 Windows 版(publish 目录,含 AOT 配置):
   ```powershell
   dotnet publish src/ALyricEase.Windows/ALyricEase.Windows.csproj -c Release -r win-x64 `
       --self-contained true -p:PublishAot=true `
       -o artifacts/vpk-publish
   ```
2. 打 Velopack 包(**版本号 = release tag**,更新说明按 tag 匹配就是靠这个):
   ```powershell
   vpk pack -u ALyricEase -v 0.11.0 -p artifacts/vpk-publish `
       --packTitle LyricEase `
       --icon src/ALyricEase/Assets/app.ico
   ```
   产出在 `Releases/`:`*-full.nupkg` + `*-delta.nupkg`(有上一版时)+ `Setup.exe`。
   **delta 只有在本地 `Releases/` 目录里留着上一版的 full 包时才会生成** —— 换机器发布
   先把历史 `*-full.nupkg` 拉回来放同一目录,否则用户每次都得下全量。
3. 发布到 GitHub Releases(**tag 必须等于 `-v` 的版本号**,`VelopackUpdateService`
   按 `releases/tags/{version}` 拉更新说明,`vpk` 也按这个规则找资产):
   ```powershell
   vpk publish github --repoUrl https://github.com/AIKA1024/ALyricEase `
       --token <gh-token> --releaseName "LyricEase {版本}" --tag {版本}
   ```
   或手动建 release:上传 `Releases/` 下的 `*.nupkg`(full + delta **都要传**,
   少了 delta 就退化成全量更新)与 `Setup.exe`,body 写更新说明。
4. 首次接入注意:现有用户(绿色版/手动安装)不经 `vpk` 安装的话 `IsInstalled=false`,
   检查更新按钮不出现;**从 vpk 打的 Setup.exe 装出来的才有自更新能力**。

## 三、验证清单(下次发布时过一遍)

- [ ] `vpk pack` 后安装 Setup.exe → 设置页出现「检查更新」按钮,版本/安装日期正确。
- [ ] 发一个更高版本 → 点检查 → 弹窗显示版本与说明 → 更新 → 下载进度 → 立即重启 → 版本变新。
- [ ] 确认 delta 生效:上一版在本地 `Releases/` 里时,新发布的资产里有 `*-delta.nupkg`,
      客户端下载明显小于全量。
- [ ] release body 为空时弹窗显示"暂无更新说明",不报错。
- [ ] 安卓:发布含 APK 资产的更高版本 → 检查 → 下载 → 立即安装 → 系统安装器拉起 →
      装完版本变新;未授权"安装未知应用"时先跳授权页,回来再点能装。

## 四、Android 自更新(2026-10-01 接入,不走 Velopack)

- **实现**:`src/ALyricEase.Android/Services/AndroidUpdateService.cs`,注册为 `IAppUpdateService`
  (替换了此前的 Null 占位)。GitHub 抓取/说明清洗与桌面共用核心库的
  `Services/Update/GitHubReleaseFetcher.cs`(JsonDocument 手工取字段,无反射,AOT 安全)。
- **链路**:`releases/latest` 对比 `PackageInfo.VersionName`(数值段比较,容忍 v 前缀)→
  挑 release 资产里 **设备 ABI 匹配的 .apk**(发布是 per-ABI 分包,`Build.SupportedAbis` 命中
  优先,兜底第一个 .apk;没有 .apk 资产 = 视作无更新)→ HttpClient 流式下载到
  `CacheDir/update/`(每次下载前清掉旧包)→ FileProvider content:// URI 调系统安装器。
- **装完即删(2026-10-01)**:系统安装器不回调应用,装成功那一刻感知不到 ⇒ 下载完成时在
  update 目录写 `downloaded-version.txt`(包版本),**应用启动时**(服务构造)发现当前版本
  已 ≥ 包版本就整目录清空;下载了但没装的包(当前 < 包版本)保留。装完当次会话内 APK 仍在,
  下次启动消失;`CacheDir` 本身系统空间紧张时可自动回收。
- **接口语义差异**:`ApplyUpdatesAndRestart` 在安卓 = 调起安装器,**不重启进程**,由用户在
  系统安装器里确认;Android 8.0+ 若未授"安装未知应用",先跳系统授权页,返回后再点一次
  「立即安装」(已下载的 APK 路径保留)。弹窗文案随平台走(SettingsViewModel 的
  `UpdateReadyDescription`/`UpdateApplyText`)。
- **清单要求**(已加,`aapt2 dump` 验过进包):`REQUEST_INSTALL_PACKAGES` 权限 +
  `androidx.core.content.FileProvider`(authority `com.aika1024.alyricease.fileprovider`,
  exported=false)+ `@xml/file_paths`(只暴露 `cache-path update/`)。
  ⚠ 改动清单后必须按 android-notes 的方法用 aapt2 验证真进了包 ——
  构建不报错 ≠ 生效(这次就先撞了"看错旧 APK"的坑:per-ABI 目录下躺着 9-26 的旧包,
  Debug 的真产物在 `bin/Debug/net10.0-android/` 根)。
- **发布要求**:GitHub release 资产里要有 APK(命名含 ABI,如 `*-arm64-v8a.apk`),
  tag 仍须是版本号(与桌面同一套约定);release body 为两端的更新说明来源。
- **首次接入注意**:现有安装的 `versionName=1.0`;发布新版本时记得同步
  `ApplicationDisplayVersion`/清单 `versionName`/`versionCode`,否则本地版本比对永远判"无更新"。

## 五、单实例(2026-10-21,与自启/更新重启共用一条闸)

**为什么加**:开机自启、手动双击图标、更新后 `Update.exe` 拉起新版本,三条路径可能同时到达。
此前全仓库没有任何 `Mutex`/IPC,来两次就起两个完整进程 —— 抢同一份 `state.json`、同一个
`app-.log`、同一个托盘图标;而关闭窗口默认只是"最小化到托盘"(`MinimizeToTrayOnClose`),
用户看不见窗口,会以为没启动,于是再点一次。**"开机后两个实例"的根因是缺这道闸,不是更新。**

| 位置 | 职责 |
|---|---|
| `src/ALyricEase/Infrastructure/SingleInstanceGuard.cs` | 命名互斥量 + 命名事件;`TryAcquire` / `AttachActivator` / `Release` |
| `src/ALyricEase.Windows/Program.cs` | `Main` → 探针分支之后、`StartWithClassicDesktopLifetime` 之前抢锁;失败直接 return;`finally` 里释放 |
| `src/ALyricEase.Windows/App.axaml.cs` | `mainWindow.Show()` 后 `AttachActivator(...)` → `DesktopLifecycleController.RestoreWindow()`(与托盘点击同一条路径) |
| `src/ALyricEase.Windows/Services/VelopackUpdateService.cs` | `ApplyUpdatesAndRestart` 前先 `Release()` |

三条硬约束:

1. **自更新重启前必须让锁**。`ApplyUpdatesAndRestart` → `Environment.Exit` → `Update.exe`
   立刻拉新进程;锁没让出去,新进程会把自己当"第二个实例",发个激活信号就退出,**更新完应用起不来**。
   代码里放在 `try/finally` 里的 `Apply` 之后那句 `TryAcquire` 只兜底"更新链没接管进程"的分支
   (`Environment.Exit` 不展开 `finally`)。
2. **所有 `--selftest-*` / `--sharetest` 必须在抢锁之前 return**。它们是独立诊断进程,
   要允许并行跑;挡在闸后面会让探针静默变成"激活首实例然后退出"。
3. **只在 Windows 启用**。`OperatingSystem.IsWindows()` 早退,Android 由系统保证单实例;
   `EventWaitHandle.TryOpenExisting` 有 `CA1416` 平台标注,`RequestActivation` 里也补了判断。

实现细节:

- 命名对象用 `Local\`(当前登录会话)而非 `Global\`;名字与 exe 路径无关,所以安装版
  (`current\`)、本地 AOT 输出(`artifacts\`)、更新前后新旧版本共用同一把锁。
- 首次持有用 `WaitOne(0)` + `catch (AbandonedMutexException)`:上一个实例被强杀时所有权直接转过来,
  否则会永远卡在"已有实例"。
- 激活等待走 `ThreadPool.RegisterWaitForSingleObject`(不额外养阻塞线程);回调在线程池,
  `App` 自己 post 回 UI 线程。
- 启动期间收到激活(窗口未建好)记 `_activationPending`,`AttachActivator` 时补发。
- 第二实例重试 20×50ms 等激活事件出现(首实例可能还在启动);等不到也静默退出,不多开。

**验证方式**(无需探针,直接起两次即可):

```bash
EXE=bin/Debug/net10.0-windows10.0.19041.0/ALyricEase.Windows.exe
"$EXE" & sleep 7; "$EXE" & sleep 5
tasklist //FI "IMAGENAME eq ALyricEase.Windows.exe" //FO CSV   # 只应有 1 个
```

2026-10-21 实测日志(两个 PID,后者 55ms 内退出):

```
[PID:36072] Single instance acquired. Mutex=Local\ALyricEase.SingleInstance
[PID:28292] Another instance is running; activation signal sent. Attempt=1
[PID:36072] Activation requested by another instance; restoring main window
[PID:28292] Another instance is active; exiting
```
