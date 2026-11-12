# 键盘快捷键

桌面端在「设置 → 键盘快捷键 → 编辑设置」查看和修改。默认只在应用窗口内生效（主窗口与桌面歌词窗口），焦点在文本框、打开模态弹窗或退出确认框时暂停分发；逐条勾选「全局」后由系统级热键接管，切到其他程序（含最小化到托盘）也响应。Android 暂不显示此入口。

| 动作 | 默认快捷键 |
| --- | --- |
| 播放 / 暂停 | 空格 |
| 上一首 / 下一首 | Ctrl + ← / → |
| 后退 / 前进 5 秒 | Ctrl + Shift + ← / → |
| 切换播放模式 | Ctrl + L |
| 音量增加 / 减少 5 | Ctrl + ↑ / ↓ |
| 打开搜索 | Ctrl + F |
| 打开 / 关闭播放详情 | Ctrl + Enter |
| 打开 / 关闭桌面歌词 | Ctrl + Shift + L |
| 显示 / 隐藏翻译 | Ctrl + Shift + T |
| 切换鼠标穿透 | Ctrl + Shift + P |

桌面歌词三条动作直接改 `DesktopLyricViewModel` 的 `Enabled` / `ShowTranslation` / `ClickThrough`，与设置弹窗（以及 Windows 悬浮窗上的按钮）共用同一份状态，改完立即落盘。Android 悬浮窗没有按钮行，只剩设置弹窗这一条入口。约定：**穿透只在有歌词窗口时才有意义**，所以歌词未显示时按穿透键会先显示歌词再进入穿透；关闭桌面歌词时 `ClickThrough` 会被一并复位（`OnEnabledChanged`），避免下次打开就点不动。

桌面歌词是独立 `TopLevel`（`ShowActivated="False"`、可穿透），焦点落在它上面时主窗口的隧道处理器收不到按键 —— 一旦开了穿透就再也解不开。因此 `DesktopLyricController` 把同一个 `HotkeyService` 实例也挂到歌词窗口的 `KeyDown` 隧道阶段；新增窗口级快捷键入口时要保持"一份 service、多个 TopLevel"。

点击动作右侧的组合键按钮，然后按下新组合键，立即保存并生效。重复组合会提示冲突并继续等待录入；Esc 取消录入，单按 Backspace / Delete 或点击 × 清除绑定。「恢复默认」重置全部动作。Tab、锁定键、Win 组合与 Alt + F4 不可绑定，Esc 保留给取消和关闭。

弹窗支持 Tab 循环导航；录入时按一次 Esc 取消，再按一次关闭，关闭后恢复此前焦点。小窗口下列表可滚动，底部操作始终保留。

## 全局（应用在后台也响应）

每行右侧的「全局」勾选走 Windows 的 `RegisterHotKey`，把组合注册到主窗口的 HWND，窗口过程收 `WM_HOTKEY` 后回到 `HotkeyService.ExecuteActionAsync` 执行。要点：

- **带修饰键，或改用 Pause / 媒体键**。`RegisterHotKey` 本身允许任意单键 —— 实测空格、字母、Insert、PrintScreen 全部返回成功，但那样会把这个键从所有程序手里抢走（空格被占就没法打字）。所以单键只在白名单放行：`Pause`、`ScrollLock`、`MediaPlayPause` / `MediaNextTrack` / `MediaPreviousTrack` / `MediaStop`（`GlobalHotkeyRules.IsSafeBareKey`）。白名单之外的单键，复选框直接禁用并说明原因 —— 想全局就加个修饰键，或者把「播放 / 暂停」改绑到 `Pause` 再勾全局（这是单键播放/暂停的正解，录音可正常录入、落盘为 `Pause`）。
- **组合可能被别的程序占着**。注册失败时复选框当场回滚并提示被占用；该动作退回"仅应用内生效"（窗口内有焦点时仍可用），不会静默失效。
- 注册成功后系统会吞掉该组合，前台窗口收不到 `KeyDown` —— 所以不会重复执行。也因此它占用了这个系统热键，其他程序此时收不到它。
- 勾选状态写在 `state.json` 的 `GlobalHotkeys`（动作 Id 数组）。启动时按它重新注册；改键会先注销旧组合再注册新的，清空绑定或「恢复默认」会连带收回全局标记。
- Android 没有注册器（`GlobalHotkeyHost.IsSupported == false`），整个「全局」列隐藏。

实现分三层：`IGlobalHotkeyRegistrar`（平台能力）、`GlobalHotkeyHost`（按 `state` 的意图做全量对账、记录注册失败）、`Win32GlobalHotkeyRegistrar`（HWND + WndProc 钩子 + VK 映射）。配置每次变化都会触发一次对账，不在按键路径上做系统调用。

配置写入现有 `state.json` 的 `Hotkeys` 字段，只保存用户覆盖项：缺少动作表示使用默认值，空字符串表示禁用该动作，无效格式回落默认值。键名使用与界面语言无关的枚举名，例如 `Control+Shift+Left`。全局标记单独存 `GlobalHotkeys`。

实现入口为 `HotkeyCatalog`（动作与默认键位）、`HotkeyService`（窗口按键分发 + 统一执行入口）、`GlobalHotkeyHost` / `IGlobalHotkeyRegistrar`（全局热键对账）、`HotkeyDialogViewModel`（录入、冲突与全局校验）和 `HotkeyDialogView`（模态焦点管理）。新增动作时同时添加清单项、执行分支和探针断言。

验证命令：

```powershell
dotnet build src/ALyricEase.Windows
dotnet run --project src/ALyricEase.Headless -- --hotkeys
dotnet run --project src/ALyricEase.Headless -- --desktop-lifecycle
dotnet run --project src/ALyricEase.Windows -- --hotkeytest
```

快捷键探针使用独立临时配置，通过 Avalonia Headless 输入验证改键即时生效、清除与重载、冲突处理、文本框和模态让位、Esc / Tab 与焦点恢复，以及桌面歌词三条动作（开关歌词、翻译、穿透，含"未开启时先显示歌词"与"关闭歌词复位穿透"）。全局热键段用假注册器验证勾选即注册、单键白名单（Pause / 媒体键放行，空格 / Insert 拒绝）、被占用回滚、改键重注册、取消/清空/恢复默认的注销、以及 `GlobalHotkeys` 落盘。`--hotkeytest` 走真窗口 + 真实 `RegisterHotKey`，验证占住组合、Pause 单键确实能注册、`WM_HOTKEY` 解析成动作 Id、重复注册不换 id、注销后可重新占用。

> ⚠ 真机探针不能用阻塞式 `Dispatcher.UIThread.RunJobs()` 等待：它只跑 Avalonia 队列，不驱动 Win32 消息循环，窗口消息会一直躺在队列里。必须 `await Task.Delay` 让出 UI 线程，否则会得到"收不到 WM_HOTKEY"的假失败。
>
> 「物理按键 → WM_HOTKEY」由系统负责，不用 `keybd_event` / `SendInput` 断言：非前台或跨完整性级别时注入会被丢弃，只会制造假失败。该环节靠手动验收（勾选全局 → 切到浏览器按组合键 → 能切歌）。
