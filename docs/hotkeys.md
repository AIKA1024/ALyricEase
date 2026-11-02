# 键盘快捷键

桌面端在「设置 → 键盘快捷键 → 编辑设置」查看和修改。仅在应用窗口内生效；焦点在文本框、打开模态弹窗或退出确认框时暂停分发。Android 暂不显示此入口。

| 动作 | 默认快捷键 |
| --- | --- |
| 播放 / 暂停 | 空格 |
| 上一首 / 下一首 | Ctrl + ← / → |
| 后退 / 前进 5 秒 | Ctrl + Shift + ← / → |
| 切换播放模式 | Ctrl + L |
| 音量增加 / 减少 5 | Ctrl + ↑ / ↓ |
| 打开搜索 | Ctrl + F |
| 打开 / 关闭播放详情 | Ctrl + Enter |

点击动作右侧的组合键按钮，然后按下新组合键，立即保存并生效。重复组合会提示冲突并继续等待录入；Esc 取消录入，单按 Backspace / Delete 或点击 × 清除绑定。「恢复默认」重置全部动作。Tab、锁定键、Win 组合与 Alt + F4 不可绑定，Esc 保留给取消和关闭。

弹窗支持 Tab 循环导航；录入时按一次 Esc 取消，再按一次关闭，关闭后恢复此前焦点。小窗口下列表可滚动，底部操作始终保留。

配置写入现有 `state.json` 的 `Hotkeys` 字段，只保存用户覆盖项：缺少动作表示使用默认值，空字符串表示禁用该动作，无效格式回落默认值。键名使用与界面语言无关的枚举名，例如 `Control+Shift+Left`。

实现入口为 `HotkeyCatalog`（动作与默认键位）、`HotkeyService`（窗口按键分发）、`HotkeyDialogViewModel`（录入与冲突校验）和 `HotkeyDialogView`（模态焦点管理）。新增动作时同时添加清单项和执行分支。

验证命令：

```powershell
dotnet build src/ALyricEase.Windows
dotnet run --project src/ALyricEase.Headless -- --hotkeys
dotnet run --project src/ALyricEase.Headless -- --desktop-lifecycle
```

快捷键探针使用独立临时配置，通过 Avalonia Headless 输入验证改键即时生效、清除与重载、冲突处理、文本框和模态让位、Esc / Tab 与焦点恢复，并输出浅色、深色及小窗口截图。退出探针另行验证退出确认期间自定义快捷键不会执行。
