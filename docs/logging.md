# 文件日志

Windows 日志位于 `%LOCALAPPDATA%\ALyricEase.Data\Logs`，可在「设置 → 关于 → 详细信息 → 打开日志文件夹」打开。使用独立数据目录，避免 Velopack 重装清空安装目录时丢失诊断文件。Android 使用应用私有数据目录下的 `ALyricEase/Logs`，暂不提供文件夹按钮。

日志通过 `Microsoft.Extensions.Logging` 接入 Serilog File sink，以 C# 显式配置。业务默认 Information，Avalonia 默认 Warning；按天及 10 MB 大小滚动，最多保留最近 14 个文件（不是保证 14 天；单条事件可略超过大小阈值）。多个进程共享文件，记录 PID；逐条刷新，不使用可能在崩溃时丢失尾部的异步队列。低频关键操作才记录，不在音频、歌词逐帧回调中写日志。

目前覆盖启动/退出、播放请求与播放器错误、登录恢复、部分登录失败、歌曲下载、应用更新，以及 UI/进程未处理异常和未观察的任务异常。异常兜底只记录，不吞掉异常或改变原有终止行为。强杀、断电、原生崩溃无法保证最后一条日志；`UnobservedTaskException` 也不保证及时触发，重要后台任务仍需主动捕获。

更新诊断包括：本地/远端版本、检查/下载/应用结果；Windows 将 Velopack 的 Information 及以上日志接入同一文件（包括版本比较、增量回退），并记录 GitHub 发布列表数量、更新通道、完整包数量、最高版本、缺失索引和请求耗时/异常。Android 记录版本比较、无版本信息/无 APK 的返回原因及安装授权/安装器启动。共用 GitHub 元数据请求记录 HTTP 状态码、解析失败和超时，不记录响应正文或更新说明。不逐次记录下载进度。

排查“明明有更新却提示最新”时，从 `Checking for application updates` 开始查看同一 PID 的记录。`Velopack returned no update` 只说明库返回了空结果；结合 `Update feed received` 的 `FullCount`、`LatestVersion` 和 Velopack 的版本比较日志判断原因。`GitHub release asset unavailable` 表示发布缺少所需索引或资产；请求异常需查看异常类型和堆栈。本次仅补诊断，不改变原有检查结果和界面文案。

服务和 ViewModel 构造函数注入 `ILogger<T>`，例如：

```csharp
logger.LogError(ex, "Playback failed. Source={Source} SongId={SongId}", song.Source, song.Id);
```

只记录明确的标量字段，不使用 `{@Object}`、反射型 enrichers 或配置文件中的 sink 自动发现。不要记录 Cookie、Token、请求/响应正文或完整歌曲对象。最终文本（包括异常）会屏蔽 HTTP(S) URL 和常见凭证字段；这只是额外保护，不能替代调用处的数据选择。提交日志前仍应自行检查其中的本地路径等个人信息。

日志目录不可写时降级为空日志，避免阻止播放器启动，Windows 设置页同时显示提示。普通文件 sink 的运行时 I/O 失败也不会抛给业务代码。默认级别与限制在 `Infrastructure/Logging/AppLogging.cs` 中配置。

当前 Serilog 的通用对象展开实现会产生一条 `IL2072` 裁剪警告（`PropertyValueConverter.CreatePropertyValue`）。本项目没有屏蔽该警告，只传显式标量，不使用该反射展开路径；文件输出仍须用下面的 NativeAOT 探针验证。以后新增 `{@Object}` 等用法时必须重新评估，不能把这次验证视为所有 Serilog 扩展都兼容 AOT。

## 验证

```powershell
dotnet run --project src/ALyricEase.Headless -c Release -- --logging
```

该探针在独立临时目录验证并发写入、异常堆栈、脱敏、级别、Avalonia 转接及文件轮转/保留，不访问用户凭证。

真实发布包支持：

```powershell
.\artifacts\aot-win-x64\ALyricEase.Windows.exe --selftest-logging "D:\Temp\ALyricEaseLogProbe"
```

正常退出后应看到 `logging-probe-complete`、20 条并发记录、Avalonia 警告和 `Application logging stopped`；不应包含 `secret-` 或 Debug 探针消息。AOT 发布需检查日志依赖引入的裁剪警告，并按版本留存原生 PDB 用于崩溃诊断。

该 Windows 探针还会离线模拟空发布列表、缺少索引、有效完整包，以及列表/索引请求失败；应看到 `logging-probe-velopack-bridge` 和 `logging-probe-update-complete`。不连接 GitHub，也不下载或安装更新。
