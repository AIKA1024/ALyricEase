# Avalonia 技巧记录

> 项目中实际验证过的 Avalonia 用法与坑,按主题累积。新技巧追加到末尾;每条尽量给项目内的落地实例。

## 点击语义:用 Tapped / Click,不要手动拼 PointerPressed + PointerReleased

**规则**:需要"点击"语义(按下+抬起才算数)时,直接用 `Tapped`(任意控件,`InputElement.TappedEvent`)或 `Click`(Button),不要自己用按下/松手事件拼状态机。

**为什么可靠**(Avalonia 12 已验证,`Gestures.cs`):`Tapped` 是 `Gestures` 在事件**路由结束后**合成的(`RouteFinished` 订阅),**完全不看 Handled**——即使 `PointerPressed` 已被祖先在隧道阶段标记 Handled,Tapped 照常发出。(11.x 老实现是 `AddClassHandler(..., handledEventsToo: true)`,结论相同。)

**tap 判定细节**:

- 按下与抬起命中**同一元素**,且抬起点落在按压点的 tap 容差内(`PlatformSettings.GetTapSize`:鼠标约 4px,触摸更大)。按住拖出后松手不触发 → 天然区分"点击"和"拖拽起点"。
- 双击的第二连击发 `DoubleTapped`,**不再**发 `Tapped` → 依赖 Tapped 的逻辑双击只算一次。
- 右键发 `RightTapped`,不触发 `Tapped`。
- 触摸从行上起手滚动只产生 Pressed/Move/Released,不产生 Tapped → 用 Tapped 不会被滚动误触。

**典型组合:祖先拦截 + Tapped**(本项目实例 `NavListTapBehavior`,挂 `NavigationPaneView.axaml` 的 NavList 上):

1. `PointerPressed` 走 `RoutingStrategies.Tunnel` 拦截并 `e.Handled = true` → 阻止子控件进入默认的按下流程(选中/按压态);
2. 动作逻辑放在 `Tapped`(冒泡)→ 点击语义白拿,同时保留对默认流程的抑制。

参照 `TrackRow.cs`:行激活用 `AddHandler(InputElement.TappedEvent, ..., RoutingStrategies.Bubble, handledEventsToo: true)`。Button 自带按下捕获 + Click 命令,更不需要自己拼。

**反面教材**(已废弃):旧 `NavigationPaneView` 在 `PointerPressed` 里直接执行分组开合 → 按下瞬间就切换、拖拽/触摸滚动全部误触、双击抖两下。

## ListBox 行选中:鼠标默认"按下即选中",触摸/笔才是抬起选中

`SelectingItemsControl.UpdateSelectionFromEvent` 经 `ItemSelectionEventTriggers.ShouldTriggerSelection` 决定触发事件:**鼠标 → `PointerPressed`,触摸/笔 → `PointerReleased`**;设 `InputElement.IsHoldWithMouseEnabled=True` 可把鼠标改到抬起。

**坑**(本项目实测,见 `AppShell.axaml` 注释):开 `IsHoldWithMouseEnabled` 会复现"特定导航顺序下歌单子项选中样式不刷新",已被关闭;所以不能靠它做"点击选中"。

**本项目方案**(`NavListTapBehavior`):隧道吞掉**鼠标**对交互行的按压(触摸/笔放行——原生抬起选中已符合点击语义,且滚动手势起手需要按下),`Tapped` 里统一驱动:分组头 → `ToggleNavGroupCommand`;普通行 → `vm.SelectedNav = item`(与 `NavigateCompact` 同路径,绑定同步 ListBox 选中态)。

## 输入事件是 Tunnel|Bubble 双策略,可在祖先抢先拦截

`PointerPressed` / `PointerReleased` / `PointerWheelChanged` 都注册为 `RoutingStrategies.Tunnel | RoutingStrategies.Bubble`,所以祖先容器可以:

```csharp
list.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
```

在子控件模板内部处理之前拿到事件并吞掉(`PointerEntered/Exited` 是 Direct,没有这个玩法)。项目实例:`SmoothWheelScrollBehavior`(隧道接管滚轮做平滑滚动)、`CtrlHorizontalWheelBehavior`、上面的 `NavListTapBehavior`(隧道防按下选中 + Tapped 驱动动作)。
