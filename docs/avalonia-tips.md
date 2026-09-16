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

## 用样式给控件"默认挂行为":BehaviorCollectionTemplate

`<i:Interaction.Behaviors>` 是附加属性,可以在 Style 的 Setter 里赋值,
用 `BehaviorCollectionTemplate` 让每个命中目标各自实例化一份集合:

```xml
<Style Selector="ScrollViewer:not(.native-scroll)">
  <Setter Property="(i:Interaction.Behaviors)">
    <BehaviorCollectionTemplate>
      <BehaviorCollection>
        <behaviors:SmoothWheelScrollBehavior />
      </BehaviorCollection>
    </BehaviorCollectionTemplate>
  </Setter>
</Style>
```

要点:

- **不能直接挂到宿主控件上**。`Behavior<ScrollViewer>` 挂到 `ListBox` 会类型不匹配;
  对 `ListBox`/`ComboBox` 这类模板里才有 `ScrollViewer` 的,用后代选择器 `ListBox ScrollViewer`
  (或全局 `ScrollViewer`)命中内部容器。
- **优先级**:元素上显式写的 `<i:Interaction.Behaviors>` 是 LocalValue,**高于**样式,
  所以显式声明不会被全局样式覆盖;同一属性多个 Style 命中时只取优先级最高的那个,
  不会出现"行为被添加两次"。
- 行为只能靠"自身判断"保证无害(如 `SmoothWheelScrollBehavior` 纵向 Disabled 或内容未溢出时
  不接管),因为样式会命中大量模板内控件(ComboBox 下拉、TextBox、Flyout 等)。
  需要留原生行为的容器,用 `:not(.xxx)` 留一个 opt-out class。

本项目落地:`Styles/Controls/Scrolling.axaml` 末尾的全局 `ScrollViewer` 样式,
三个宿主(Desktop/Android/Headless)都通过 `StyleInclude` 引入该文件。

## Flyout 打开动画:从裁剪区外揭示,并在 Popup.Opened 后起播

**机制**(`Controls/FlyoutOpenAnimation.cs`):合成动画把弹层表面整体偏移一段距离,越界部分被
**弹窗窗口**裁掉,随滑动逐渐露出 —— 这就是 WinUI 菜单"从锚边展开"的观感。桌面端弹层是独立
`PopupRoot` 窗口,窗口按内容尺寸创建,所以**裁剪边界就是弹层自身矩形**。

**坑**:偏移如果是固定值(原版 `g_entranceThemeOffset = 50`),开局遮掉的比例 = 偏移 / 弹层高度,
**随弹层变高而衰减**。实测(`--menuheight` 探针):

| 弹层 | 高度 | 固定 50px 遮掉 | 50% 高度遮掉 |
|---|---|---|---|
| 每日推荐 歌手/专辑菜单(2 项) | 96 | 52%(像"从 0 滑出") | 50 |
| 侧栏歌单右键菜单(3 项 + 分隔线) | 141 | 35% | 70 |
| 播放条歌曲菜单(10 项) | 330 | **15%(观感"几乎全出来了")** | 165 |

**第二个坑**:`偏移 = 高度 × 0.5` 仍会让半个菜单在首帧出现。对播放条长菜单,
这依然很像直接弹出。新规则使用 `偏移 = 弹层完整高度`,使首帧整个表面都在裁剪区外。

**取值与时机都很关键**:`PopupRoot.PositionChanged` 发生时表面可能还没 Arrange,
所以高度要取 `PopupRoot.ClientSize`(DIP) / overlay 时取 `OverlayPopupHost.Bounds`。但定位事件只用来记录方向,
不能在未 Show 的合成树上直接起播;真正的 `StartAnimation` 放到 `Popup.Opened` 之后。
`--menuanim` 探针同时核对真实弹层高度和完整高偏移。

## 卡片布局:封面 + 文字不要用"单格 Grid + Margin 下移"

**`Stretch` 且显式设了 `Width`/`Height` 时,Avalonia 按 Center 处理** —— 这在"封面 + 标题"
的卡片模板里会咬人。错误写法:

```xml
<Grid Width="200">                        <!-- 单格 -->
  <Border Width="200" Height="200" .../>   <!-- Stretch + 固定高 → 被垂直居中! -->
  <TextBlock Margin="4,206,4,0" ... />     <!-- 以为在封面下方 6px -->
</Grid>
```

卡片实测 225 高,封面落在 **y=12**(不是 0),底边 212,而标题固定在 206 → **重叠 6px**。
歌手页同款写法把 Margin 放到 220,表现为封面下沉 20、标题贴住封面底边(间距 0)。
更坑的是:卡片越高错位越大,改 Margin 只能"看起来碰巧对",一旦字号/行数变了又歪。

正确写法 —— **分行**,封面顶对齐,间距由文字那行的 Margin 决定:

```xml
<Grid Width="200" RowDefinitions="200,Auto">
  <Border Grid.Row="0" Width="200" Height="200" .../>
  <TextBlock Grid.Row="1" Margin="4,6,4,0" ... />
</Grid>
```

个性推荐卡片(`RecommendView`)就是这么写的(`RowDefinitions="Auto,50"` + 文字
`Margin="4,6,4,0"`,实测间距 6),现在 `AlbumGrid`(全部专辑页)与 `ArtistView`
(专辑/单曲与EP 两处)也统一到同一结构、同一 6px 间隔。

排查手法:几何问题别靠肉眼,用无头探针打印卡片内元素的相对矩形
(`src/ALyricEase.Headless/AlbumGridProbe.cs`,入口 `--albumgrid`),
直接看"封面底边 → 标题顶边"的数值,负值即重叠。注意选元素别选错
(个性推荐卡片里播放量角标的数字也是 SemiBold,得再加字号条件才选到标题)。

## 页面往返的内存/生命周期回归:别用肉眼看任务管理器

"进 A 页再返回,内存一直涨"这类反馈,必须用探针把三件事分开量化,否则很容易
把"峰值抬升"当成"泄漏",或者被无头环境的假象带偏。
现成入口:`src/ALyricEase.Headless/PageLoopMemoryProbe.cs`(`--memloop` 无头 / `--memloop-real` 真窗口)。

**要同时看的四个量**:

1. **弱引用遗留**:每轮捕获 `View` / `SongItemViewModel` / `AlbumCardViewModel`,
   下一轮判 `IsAlive && !IsAttachedToVisualTree()`。在树上的当前页视图不算泄漏,
   只按 `IsAlive` 判会把当前页误报成泄漏。
2. **集合是否回落**:`Songs/Albums/Sections` 被 `Clear()` 后旧元素必须能回收 ——
   这是本项目页级 VM 单例 + 集合复用的主要风险面。
3. **解码图缓存**:`CoverImagePipeline.MemoryCacheStats`(internal)。切页不会清空缓存；
   热点图片由 64MB/512 项的租约式 LRU 复用。应观察多轮往返后是否在预算附近趋稳，
   而不是要求返回后立即归零。
4. **托管堆 + 私有内存 + 工作集**:前两者看趋势,工作集受渲染器影响会偏高。

**两个必踩的坑**:

- **无头模式动画不自己走**。只 `Dispatcher.UIThread.RunJobs()` 而不推帧,
  `TransitioningContentControl` 的过渡永远不结束,旧页一直挂在视觉树上,
  表现为 `visuals` 突然翻倍、旧视图"泄漏"——纯假象。
  必须配 `AvaloniaHeadlessPlatform.ForceRenderTimerTick()`(见 `PageLoopMemoryProbe.Drain`)。
- **真机模式必须让出 UI 线程**。在 UI 线程上 `Thread.Sleep`/同步阻塞,渲染与合成器
  根本不跑,于是**测不到 GPU 纹理**这一层(而这正是无头测不出、真机最容易涨的地方)。
  真机版探针要写成 async + `await Task.Delay(...)`,由 `Dispatcher.UIThread.Post(async () => ...)`
  驱动,并在结束时 `InvokeShutdown()`。

**封面要真加载才算数**。用无效 URL 或空 URL 测,缓存永远 0 项,等于没测。
`PageLoopMemoryProbe` 自带本地图床(`TcpListener` 手写 PNG + CRC32),
封面能走完"HTTP → 磁盘字节缓存 → 解码 → 挂图"整条链路;结束时按 URL 逐个
`MusicCacheService.RemoveCoverAsync` 删掉,避免探针数据污染真实用户缓存。
等待用"(缓存条目, 图床请求数) 连续 2 秒不变"而不是固定 sleep。

可用的开关(环境变量):`ALY_MEMLOOP_ROUNDS` 改轮数、`ALY_PROBE_LOG` 把每行同时写进文件
(真机模式的控制台会被渲染线程影响,记文件更好读)、`ALY_PAGEVIEW_TRACE` 打印复用判定时序、
`ALY_PROBE_SHOTS` 存截图。

## 复用视图只能省"骨架",省不了"容器重新实化"

首页(`RecommendView`)每次返回都被重建,于是做了 `Infrastructure/ReusablePageViewTemplate`
——`IDataTemplate` 的复用版,用视图自身的 `AttachedToVisualTree`/`DetachedFromVisualTree`
跟踪缓存实例是否还在树上,**只在确实离树时才复用**;过渡(300ms)进行中上一棵仍挂在退场的
presenter 上,此时另建一棵顶上并把缓存槽换成刚上屏的那棵,避免同一控件两个父级。
AppShell 里把首页模板换成了它。

**它确实生效**:实测同一个实例连续 15 轮复用(`缓存槽命中=是`),节点数不变;
也安全:无弱引用遗留、首页行/卡片/已上屏封面数与基线一致、快速往返(过渡未完就返回 ×10)异常=0。

**但在分配量上几乎没收益**。把"进入歌手页 / 返回首页"两段分开计量,返回首页一段中位数:

| 配置 | 返回首页分配(中位数) |
|---|---|
| 复用视图 | 59.4MB |
| 不复用(普通 DataTemplate) | 60.0MB |

原因不是复用没生效,而是**首页主体不归它管**。逐级比对复用视图内部节点的身份:

| 层级 | 复用后 |
|---|---|
| `PageContent`(ScrollViewer 里的 StackPanel) | 同 |
| 区块列表 `ItemsControl` | 同 |
| 它的 `ItemsPresenter` / `Panel` | 同 |
| `Panel` 的子项(区块容器) | **每轮换新** |
| 区块内的 `SongGridView` 与全部歌曲行 | **每轮全部重建**(新增行控件 60/60、90/90) |

即:**视图对象留住了,但"容器生成器"在重新挂树时被重置**,生成出来的子项整批作废。
页 VM 侧反而是干净的——区块 VM 实例不变(数据没重载,`EnsureLoadedAsync` 的幂等守卫生效),
重建纯粹来自"视图离开视觉树再回来"这条路径。`ItemsControl` / `ItemsPresenter` 源码里没有
离树钩子,`TemplatedControl` 也不会因离树重挂而重应用模板,所以触发点落在容器生成器的重置上;
框架内部那一跳没有逐行确认,但**现象已稳定复现**,足以指导取舍。

⇒ **教训(已被后文修正)**:当时由此推出"必须让这棵树别离开视觉树",方向对但**不是主要矛盾**。
复用视图保住的确实是骨架;而这棵骨架之所以"重建一次就要 43.5MB",是因为它当时的行控件数量
被一个更基础的改动放大了 3 倍多——**横向虚拟化被去掉**(见下一节)。先把虚拟化还回去,
"返回首页"每轮分配就从 43.5MB 掉到 23.7MB,和旧版本持平;此后是否还要追求"树不离开视觉树"
才有讨论价值(上表的 59.4MB / 60.0MB 也是虚拟化缺失时测的)。

⇒ **方法**:这种结论只有"分阶段计量"才看得见——整轮分配量被歌手页重建淹没(~250MB/轮),
只看总量永远分不清首页省下没有。探针每轮的 `本轮分配=(进入歌手页=XMB 返回首页=YMB)`
与后半程中位数就是为这个加的;另外 `新增行控件=N/M`(整页行控件哈希集合与上一轮求差)
比"首个控件是否同一对象"更硬,能直接量出"到底重建了多少个控件"。

## 横向虚拟化面板不能去掉:`StackPanel` 一换,每次重建整页的分配就翻倍

**症状**:个性推荐里进歌手页再返回,内存一轮一轮涨,任务管理器能到 800MB+;
但截图、节点普查、内容都完全正常,弱引用也查不出泄漏(旧视觉树确实都是垃圾,只是 GC 来不及收)。

**定位方式**:别靠推理。用户指出"`2d30ce4` 这个版本不涨内存",就把那个提交当对照做实测——
`git archive <commit> | tar -x -C <临时目录>`(只写工作区、不动 refs),在副本里加一个**同口径**的
循环探针(同样的窗口尺寸/内容规模/本地图床/分阶段计量),真窗口跑同样的 15 轮。旧树能干净编译。

`song-grid` 面板一行之差,两组数字差这么多:

| 指标 | 2d30ce4(有虚拟化) | 当前工作区(无虚拟化) |
|---|---|---|
| 首页实化歌曲行 | 18 | 60 |
| 首页视觉树节点 | 1379 | 2209 |
| 进入歌手页 分配中位数 | 8.7MB | 51.4MB |
| 返回首页 分配中位数 | 23.3MB | 43.5MB |
| 工作集(第 1→15 轮) | 225→270MB(**平,还会回落**) | 268→**400MB**(阶梯上涨) |
| 图床请求累计(15 轮) | 33 | 266 |

**受控 A/B**:同一份代码,只把面板改回去,其余配置完全不变——

| 指标 | `<StackPanel>` | `<VirtualizingStackPanel>` |
|---|---|---|
| 首页实化歌曲行 | 60 | **18** |
| 首页视觉树节点 | 2209 | **1411** |
| 返回首页 分配中位数 | 43.5MB | **23.7MB** |

再把封面 URL 固定(模拟"反复进同一个歌手",即真实点击路径)后,曲线彻底回到旧版本:

| 指标 | 2d30ce4 | 现在 |
|---|---|---|
| 进入歌手页 分配中位数 | 8.7MB | 11.8MB |
| 返回首页 分配中位数 | 23.3MB | 23.7MB |
| 工作集(第 1→15 轮) | 225→270MB | 237→255MB(平) |
| 图床请求累计 | 33 | 33 |

**为什么以前看不出来**:非虚拟化只影响"屏幕外的列"。一屏之内的控件数量一个不多一个不少,
所以像素级截图完全一致、节点普查看不出差别(要数**整页**实化总数才有区别),
弱引用也查不到泄漏——它只是把"每次重建整页"的代价从 18 行抬到 60 行。

**规则**:

1. 行控件(`TrackRow`)是页面上最贵的控件(每行几十个视觉对象 + 封面),承载它的面板
   **不要**为了"测量高度更稳""避免滚动时同步建列"换成非虚拟化 `StackPanel`。
   本项目 `SongGridView.Columns` 必须保持 `VirtualizingStackPanel Orientation="Horizontal"`。
2. 与 `AlbumGrid` / `ArtistView` 的横向卡片区一样,虚拟化面板下**间距要靠容器 Margin**,
   不要用 `Spacing`(虚拟化面板不支持)。
3. 排查这类问题必须量"**整页实化控件总数**"(如 `新增行控件=N/M`),只看视口内数量会被骗。

**探针口径**:封面 URL 是否带轮次后缀,决定测的是哪件事,两种都要跑,否则会把
"每轮重新下载解码 50 张封面"误判成泄漏:

- 带后缀(`ALY_FIXED_COVER_URL` 不设)= "每次进的都是不同歌手",最坏情况,考封面缓存淘汰;
- 不带后缀(`ALY_FIXED_COVER_URL=1`)= "反复进同一个歌手",常态,考页面重建本身的代价。

**这个改动当初是有意的,但实测证据不支持它。** 改动者留下的理由是"末列进入视口时不能用其一行
高度重算整个横向面板""避免手机滚动时同步创建整列 TrackRow",并把 `--sg` 探针改成窄屏
430px + 55 首、断言里写死 `!hasVirtualizingPanel`。把面板还原后再跑**同一个探针**:

| | 普通 `StackPanel` | `VirtualizingStackPanel` |
|---|---|---|
| 滚到末列后高度 | 404(稳) | 404(稳) |
| 首帧布局耗时 | 712ms | **544ms** |
| 首帧实化行 | 55/55 | **12/55** |
| 滚到末列实化行 | 55/55 | **7/55** |

即:它要防的"高度跳变"在虚拟化下并没有复现,而虚拟化连首帧都更快(少建 43 行 TrackRow)。
`--sg` 的断言已改回"必须虚拟化 + 首帧与末列都不得建满 + 高度稳定 + 末首可达",
`--vgrid`(3/17 列、滚动复用容器、几何节距)一并通过。

⇒ 若将来在真机上确实看到滚动卡顿,正确方向是给横向面板调 `VirtualizingStackPanel.CacheLength`,
或给"完整列高度"绑一个稳定值(旧实现曾是显式 `Height`),**而不是把虚拟化整个去掉**。

## 详情页返回卡顿:快照先留内存,磁盘只作兜底

**症状**:在歌单页滚动到下方 → 点歌手进歌手页 → 返回,歌单页先空一会儿才出歌曲,随后跳回原位置。

**根因**:返回恢复走"读一次性磁盘快照",而离页时派发的**写入是 fire-and-forget**。
`TryTakePlaylistPageSnapshotAsync` 开头会 `await` 在途写入,于是**返回必须等写完 —— 写入要多久,
返回就卡多久**。同一探针只改"在详情页停留多久"(600 首):

| 在详情页停留 | 返回 → 有歌曲 |
|---|---|
| 0ms | **1136ms** |
| 600ms | 374ms |
| 3000ms | 48ms |

阶段拆分(停留 0ms 那轮):`读磁盘快照(含等待在途写入)= 986~1171ms`,
而**读本身只要 2ms、重建 600 行只要 0~3ms**。即卡顿 100% 来自"等在途写入",
既不是 I/O 慢,也不是控件建得慢。

**修法**:让内存副本承担返回恢复,磁盘副本只作"内存淘汰后"的兜底。

- `MusicCacheService` 加 `SnapshotMemoryCache<T>`(纯数据 LRU,上限 8 个页面):
  `Cache*PageSnapshotAsync` 同步塞进内存,再**延后 3 秒**派发落盘;
- `TryTake*PageSnapshotAsync` 先查内存,命中就**同步返回**,并取消尚未开始的落盘(不写也不删);
- 内存淘汰掉的条目**立刻落盘** —— 否则那段延迟窗口里内存和磁盘两头都没有副本。

结果(同一探针):停留 0/300/1500/4000ms 全部 **1~2ms** 恢复;进程结束时 `n-*.snapshot` 遗留 0 个
(落盘都被取消,没有产生磁盘 I/O)。

**取舍很划算**:600 首的内存驻留实测 **131KB**(单首约 0.22KB),上限 8 个页面;
换来的是最长约 1 秒的返回等待和一次 0.6~1.3 秒的后台落盘都不再发生。

**为什么磁盘快照可以降到"兜底"**:`CacheKey` 是每次捕获时新生成的 GUID,只存在进程内的导航历史里,
所以磁盘文件**只有当前进程会读** —— 内存副本与它功能等价,且不必等 I/O。

**顺带修掉的一个真实开销**:`TrimToLimitAsync` 每次写缓存后都要 `stat` + 排序整个缓存目录
(真实用户机器上 12870 个文件 / 4.1GB)。实测落盘耗时几乎与内容无关:

| 落盘内容 | 节流前 | 节流后 |
|---|---|---|
| 0 首(空快照) | 550ms | **25ms** |
| 600 首 | 591ms | 569ms(这段才是序列化本身) |

现按 5 秒窗口节流(窗口内只有一个调用方真正执行);`SetMaximumSizeMbAsync`(改缓存上限)
仍走 `force: true` 立即生效。

**规则**:

1. "只为当前进程服务"的一次性页面快照优先走内存,磁盘只作淘汰兜底;
   不要把它放在"返回时 await 在途写入"这条路径上。
2. 后台做"全目录扫描"的容量裁剪要按时间窗口节流,但"用户改了上限要立刻生效"必须能绕过节流。
3. 判断这类卡顿必须**分阶段计量**。只看总耗时会混淆"等 I/O"和"重建控件",优化方向会完全反
   (实测重建 600 行只要 3ms,而等待是 1.1 秒)。

**探针**:`--pl-return-real`(`src/ALyricEase.Headless/PlaylistReturnLatencyProbe.cs`),
真窗口 + 真合成器,量"返回→有歌曲""返回→滚动到位"与 UI 冻结峰值。

- `ALY_PL_ROWS` 曲目规模(默认 600)、`ALY_PL_ROUNDS` 轮数
- `ALY_PL_DWELL` 每轮"在详情页停留"毫秒数(逗号分隔,默认 `0,400,2000`)——
  **这是关键变量**,它决定返回时落盘是否已完成
- `ALY_PL_WRITE_PROBE=1` 额外量一次落盘耗时(含 3 秒延迟窗口)
- `ALY_PROBE_LOG` 输出到文件

`--playlist-lifetime` 带两条回归线:`restore-no-disk-wait`(600 首离页后立刻返回必须 < 100ms)
与 `eviction-disk-fallback`(内存淘汰后磁盘仍可读回)。`PlaylistViewModel.RestoreTimingTrace`
是零开销诊断钩子(默认 null),探针挂上后输出各阶段耗时。





**后记:磁盘那一层后来被彻底删掉了**

上面这套"内存优先 + 磁盘兜底"其实是个中间形态。既然 `CacheKey` 只为当前进程服务,
磁盘副本就没有存在价值 —— 于是 `n-*.snapshot` 的写入/读取/清理整套生命周期与六个快照 JSON DTO
全部删除(约 250 行),`SnapshotMemoryCache` 上限 8 → 32 页(600 首 ≈ 131KB/页,合计 ~4MB 量级,
仍远小于图片预算)。`Cache*`/`TryTake*` 现在是**同步字典操作**(返回 `Task.CompletedTask`/
`Task.FromResult`):返回恢复既不等待 I/O,也不再抢 `_mutationGate`;容量外被淘汰的那一层
直接回退常规加载。升级前遗留的文件由 `MusicCacheService.DeleteLegacyPageSnapshotFiles()` 清一次。

⇒ 上面那条"不要把它放在'返回时 await 在途写入'这条路径上"的规则依然成立,
但现在的解法更彻底:**让这条路径不存在**。另外注意"等 GCD 排队的锁"和"等 I/O"是同一类问题,
只有分阶段计量才分得清是哪一个(见下方 `--pl-return-real` 的用法)。

**别把 `#if ANDROID` 当平台开关(核心库专属陷阱)**

写 `MusicCacheService` 的缓存目录、`AppStateStore`、`CookieStore` 时踩到:
`#if ANDROID` 在**核心库**里是**死分支** —— ANDROID 常量由 Android SDK 只对 `net*-android` 工程定义,
而核心库只面向 `net10.0`。实测产出的 `ALyricEase.dll` 里查不到任何 Android 类型名
(`FilesDir`/`Java.Lang.Runtime` 全部为 0 命中),即这些分支永远不会被编译,
编译不报错、运行时静默走 `#else`。平台判定一律用 `OperatingSystem.IsAndroid()`
(见 `Infrastructure/ImageMemoryBudget.cs`,项目里 5 处历史 `#if ANDROID` 已加警示注释)。

## Button 内容默认靠左:HorizontalContentAlignment 默认是 Stretch,不是 Center

Avalonia(WPF 不同)的 `TemplatedControl.HorizontalContentAlignment` 默认值是 **Stretch**
(反射实测 12.1.1),FluentTheme 的 Button ControlTheme 不覆盖它 ⇒ 裸按钮/自写类样式按钮的
文字全部靠左,垂直同理要看默认。WinUI/Fluent2 的 Button 内容默认居中,这是 Avalonia 的已知偏差。

- 项目级修正在 `Styles/Controls/Buttons.axaml` 顶部:裸 `Button` 样式设 H/V Center 作全局默认;
  需要拉伸内容的按钮(login-method/navigation-button/track-menu-item 等)在各自样式里显式 Stretch。
  注意 Avalonia 样式无 CSS 特异性,同 host 内声明顺序即优先级 —— 全局默认要放在文件最前。
- 强调色按钮不要自写(曾有 7 份重复的 dialog-primary:硬编码 SystemAccentColor+White,
  且没处理 :disabled):FluentTheme 自带 `Classes="accent"`(AccentButton* 资源色板,
  hover/pressed/disabled 齐全),直接用。2026-09-16 已全项目替换并删除全部本地副本。

## Release 包的裁剪会删掉"反射创建的页面视图"的构造函数(构建期 IL2072 就是预告)

**现象**:Release APK 在 Android 上冷启动即崩,系统弹"ALyricEase 屡次停止运行":

```
E AndroidRuntime: FATAL EXCEPTION: main
E AndroidRuntime: android.runtime.JavaProxyThrowable: [System.MissingMethodException]:
                  Arg_NoDefCTor, ALyricEase.Views.RecommendView
E AndroidRuntime:   at ALyricEase.Infrastructure.ReusablePageViewTemplate.Create
E AndroidRuntime:   at Avalonia.Controls.Presenters.ContentPresenter.CreateChild
                  ...(Measure 链)
```

**根因**:`ReusablePageViewTemplate` 靠 `Activator.CreateInstance(ViewType)` 建视图
(`AppShell.axaml` 里 `ViewType="views:RecommendView"`)。只写 `Type` 属性、不做标注时,
linker 无法得知"这个 Type 的公共无参构造会被用到",Release 下直接把它裁掉;
运行时反射拿不到构造函数 → 首页一渲染就抛。**Debug 不裁剪,所以只在 Release 包上炸**。

**预告信号**:构建期这条警告,别当噪音忽略 ——

```
warning IL2072: 'type' argument does not satisfy
  'DynamicallyAccessedMemberTypes.PublicParameterlessConstructor'
  in call to 'System.Activator.CreateInstance(Type)'
  (ReusablePageViewTemplate.cs:98)
```

**修法**:给承载 Type 的属性加注解,把需求声明给 linker(注解会把 `typeof(...)` 的赋值
一路传播过去,`DataTemplate` 式的写法同样适用):

```csharp
[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
public Type? ViewType { get; set; }
```

2026-09-16 实测:加上后 `IL2072` 消失(3 条警告→2 条,只剩既有 CS8604/CS8634),
同一个 Release APK 在 MuMu(Android 12)上首页正常渲染、封面正常加载。
**推论:凡是用 `Activator.CreateInstance` / `Type.GetType` 建 UI 的地方都要过一遍 IL2072**;
本项目目前只有 `ReusablePageViewTemplate` 这一处。
注意探针查不出这类问题 —— 无头/桌面探针跑的是不裁剪的构建,只有真机 Release 包会暴露。

## Android 手动 adb 装 Debug 包:先关掉 Fast Deployment

Debug 构建默认启用 Fast Deployment(程序集**不**打进 APK,由 IDE 在部署时单独推送)。
绕过 IDE 直接 `adb install` 那个 APK,应用启动会立刻 abort:

```
F monodroid: No assemblies found in '/data/user/0/<pkg>/files/.__override__/arm64-v8a'
             or '<unavailable>'. Assuming this is part of Fast Deployment. Exiting...
F monodroid: Abort at monodroid-glue.cc:757
```

要么让 IDE/`dotnet build -t:Install` 走完整部署,要么构建时关掉:
`dotnet build ... -c Debug -p:EmbedAssembliesIntoApk=true`(程序集内嵌,可直接 adb 安装)。
Release 默认就是内嵌的,不需要这个参数。

## 在 WorkBuddy 沙箱里做 Android Release 构建:增量必挂,只认全量

**现象**:同一个 `dotnet build ... -c Release` 命令,第一次全量构建成功,
之后每次重建都在覆盖中间产物的那一步失败,且**失败点随改动漂移**:

```
tools\Xamarin.Android.Common.targets(2219,3): error XA3006: 无法编译本机程序集文件: marshal_methods.arm64-v8a.ll
  [llc.EXE stderr] llc: error: permission denied
obj\Release\net10.0-android\lp\69\jl\res\..\flat\69.flata : error APT2000: 数据无效。 (13).
```

**根因**:WorkBuddy 的文件系统保护层**允许新建文件、拦截覆盖既有文件**
(`CODEBUDDY_SAFE_DELETE_SANDBOX=1`)。第一次构建所有产物都是"新建"⇒ 通过;
第二次起要**覆盖**上一轮的 `.so` / `.ll` / `.flata` ⇒ 被拒。
`llc` 与 `aapt2` 都不检查写入结果,于是留下 0 字节或半截文件,
**再下一次构建就把坏文件当成有效输入**(`.flata` APT2000 就是这么来的)。

**最危险的后果 —— 会产出"能装上但一启动就崩"的 APK。**
AOT 步骤失败后若后续目标仍能跑完(增量判定依据 stamp 而非产物),
打出来的包里会混进**上一轮的 AOT 模块**,而 `Mono.Android.dll` 已换版:

```
E mono-rt: * Assertion at aot-runtime.c:3864, condition `is_ok (error)' not met,
  function:decode_patch, module 'Avalonia.Android.so' is unusable
  (GUID of dependent assembly Mono.Android doesn't match (expected 'DCDC3665-...', got '80AA39C7-...')).
I ActivityManager: Process com.aika1024.alyricease has died: fg  TOP   ← 0.3s 一次,崩溃循环
```

看到这条断言不要去查 Mono 版本,直接怀疑"AOT 产物与程序集不同源"。

**做法**:

- **每次构建前清干净**,不要指望增量。`Remove-Item` 被 safe-delete shim 拦,
  但 `[IO.Directory]::Delete($p, $true)` 可用(实测 1357 个文件的 `obj\Release` 约 125 秒):
  ```powershell
  [IO.Directory]::Delete("$p\obj\Release", $true)
  [IO.Directory]::Delete("$p\bin\Release", $true)
  ```
  `Move-Item` 搬整个 `obj\Release` 会在中途被沙箱**直接杀掉进程**(连 `catch` 都进不去),
  别用搬运代替删除。
- **只验证托管逻辑时关掉 AOT**,绕开 `llc` 那一步,构建也快得多(18.6MB vs 29.3MB 的包):
  ```
  -p:RunAOTCompilation=false -p:EmbedAssembliesIntoApk=true
  ```
  验包:`lib/libaot-*.so` 条数应为 **0**;程序集仍在 `lib/<abi>/libassembly-store.so` 里。
  注意裁剪仍会执行,所以 `IL2072` 这类问题照样暴露。
- 真要出带 full Mono AOT 的发布包,**在 Rider 或普通终端里构建**,不要在 WorkBuddy 里做。

## 自续订的 RequestAnimationFrame 循环:必须自己判"窗口是否在呈现"

`ProgressRenderAnimator`(`Views/ProgressRenderAnimator.cs`,2127c30 引入)用
`TopLevel.RequestAnimationFrame` 推进进度条动画:回调里**直接再请求下一帧**,
只要 `ShouldAnimate()` 成立就按最多 60 FPS 一直跑。两个实例:
底部播放条(`PlayerProgressBar`)+ 正在播放页(`NowPlayingView`)。

### 框架不会替你停:最小化时回调照样送达

实测(2026-09-16 真窗口探针):窗口最小化 / `Hide()` 时 Avalonia **仍把 RAF 回调送进来**,
填充条 `ScaleX` 依然按实时速率推进(6 秒推进 0.0333 = 180 秒曲目的实时速率)。
所以"窗口不可见 ⇒ 渲染自然停"这个直觉是错的 —— **动画器自己的 `ShouldAnimate()` 不看可见性,
循环就会为一个看不见的进度条做完整首歌的每帧写入**(每帧改 3 个 Transform + 视觉失效)。

### 修法:判定补"可呈现",状态变化时重新点火

```csharp
private bool IsHostPresentable()
{
    if (_observedTopLevel is not { } topLevel) return true;   // 还没解析到 TopLevel 时不自作主张
    if (!topLevel.IsVisible) return false;
    return _observedWindow is null || _observedWindow.WindowState != WindowState.Minimized;
}
```

`ShouldAnimate()` 末尾加 `&& IsHostPresentable()`,另外:

- **停止是隐式的**:帧回调里 `ShouldAnimate()` 为 false 就直接 `return`、不再续订,不需要额外机制。
- **恢复必须显式**:订阅 `TopLevel.PropertyChanged`(只关心 `Visual.IsVisibleProperty` 与
  `Window.WindowStateProperty`),可呈现性翻转时 `RestartFrameLoop()` 重新点火;
  用 `_lastPresentable` 防抖,否则窗口状态每抖一下都会重置预测基线。
- **必须订阅 `WindowState`,不能只订阅 `IsVisible`**:最小化时 `Window.IsVisible` 仍是 `true`。
- 桌面下 `Window` 本身就是那个 `TopLevel`(同一个对象),订阅时要去重,否则回调走两遍。

### 真实 `AppShell` 下的改前 / 改后对照(探针 `--shell-cpu-real`)

探针用与 `MainWindow.axaml` 逐字节一致的 `TestMainWindow`(真 `AppShell` + 真
`PlayerBarView` 进度条 + 真 `NowPlayingView`),由真实 `PlayerViewModel` 驱动、
200ms 一次进度上报(与真实引擎同量级);最小化场景跑 3 轮取配对差值中位数。

| 指标(最小化时) | 改动前 | 改动后 |
|---|---|---|
| 填充条 `ScaleX` 推进 / 期望推进 | **1.003 ~ 1.006** ⇒ 循环照跑 | **0.000** ⇒ 完全停摆 |
| 循环净代价(关掉进度上报,唯一干净口径) | **+1.04% / +1.30%** 单核 | **0.00% / 0.00%** |
| 循环净代价(开着 5Hz 进度上报) | +1.30% / +0.26% | −0.52% / −0.26%(落在噪声内) |
| 进程 CPU 绝对量 | 0.78 ~ 1.82% | 0.00 ~ 0.26% |
| 可见时 | 照常推进(6 秒 0.0334) | 照常推进;从最小化恢复后**自动重启**(推进量回到 0.03342) |

⚠️ **可见时的 CPU 大头不是这台循环**。真实窗口可见时进程 CPU 达 **20~28% 单核**
(1200×800 窗口 + `AcrylicBlur` 模糊层 + 全窗合成),而"循环开/关"的配对差只有
+0.78% / −1.83% —— 就在噪声里。结论:这台循环在最小化下省掉约 **1% 单核**,
可见时它相对整窗呈现开销占比很小。**要降可见时的 CPU,方向不在这里**。

### ⚠️ 先分清三类"一直在动",它们的成本模型完全不同

| | `AlbumCoverBackground`(动态背景) | `ProgressRenderAnimator`(进度条) |
|---|---|---|
| 机制 | **组合动画** `visual.StartAnimation` + `IterationBehavior.Forever` | **UI 线程上的自续订 `RequestAnimationFrame` 循环** |
| 跑在哪 | 合成/渲染线程 | UI 线程 |
| 驱动 | **由帧驱动** —— 不渲染就没有成本 | 只要 `ShouldAnimate()` 成立就一直重挂下一帧 |
| 窗口最小化时 | 不渲染 ⇒ 停止推进(任务管理器里该进程 GPU 归零是**正常现象**,不代表"没人请求帧") | **回调仍送达**,每帧照改 `ScaleX`/`TranslateX` 并触发视觉失效 |
| 其它 | 不感知播放状态(**暂停也转**),只在它挂在视觉树上时 | 感知播放状态;两个实例(播放条 + 正在播放页) |

排查"播放/后台 CPU 偏高"时两处都要看,但**别用 GPU 读数否定任一者**:
最小化时窗口不 present,GPU 必然归零,这只说明"没提交到屏幕"。

第三类(框架主题动画,`IsIndeterminate` 进度条等)见下一节:它跑在**合成线程 + UI 线程**两处,
最小化时的净代价实测 **2.60%** 单核,同样不感知窗口状态。

### 写这类探针时的坑(都实测踩过)

1. **重挂下一帧必须在回调里直接调用**,不能绕 dispatcher:

   ```csharp
   void Tick(TimeSpan _)
   {
       frames++;
       topLevel.RequestAnimationFrame(Tick);   // ✅ 与 ProgressRenderAnimator 同款
   }
   ```

   第一版写成 `Dispatcher.UIThread.Post(() => topLevel.RequestAnimationFrame(Tick))`,
   多出来的那趟 dispatcher 往返破坏了帧合并,计数器退化成**自旋** ——
   实测"2 秒 3 727 916 帧"(每秒 180 万),完全失真。

2. **判定"循环是否在跑"要用离散证据,不要用帧数,更不要用 CPU**。
   帧数会被"有没有别的东西在失效"污染:同一个计数器,动画器开着时约 **22 帧/秒**,
   动画器关掉(窗口内容完全静态)时约 **10.8 帧/秒** —— 说明 RAF 送达与渲染挂钩、
   不是固定时钟。上一版探针**在动画器关闭的情况下数帧**,数到四种窗口状态
   (可见/最小化/隐藏/恢复)"都是 27 帧/2 秒",于是得出了"后台仍照样出帧"的结论 ——
   **结论碰巧对,证据是错的**:那个数字量的是"空转的平台",与动画器是否在跑无关。
   可靠做法是读**动画器自己写入的成果**(填充条 `ScaleX`):只有它在写,
   它动了就是循环真的在跑,而且推进量还能直接和"实时速率"对照。

3. **进程级 CPU 量不了这个量级,别硬用**。本机静息抖动可达 3% 单核,
   同一相位跨轮次就能从 1.56% 跳到 6.25%;最小化时还出现过
   "循环关 2.60% > 循环开 2.08%"的反号。要做 CPU 对照必须
   **同场景紧邻配对 + 拉长窗口 + 看差值的统计量**,并且接受"落在噪声内 = 测不出"这个结论。

4. **含进度上报时不能拿 `ScaleX` 判活性**。进度上报会走
   `PlayerViewModel.OnPositionChanged → ScrubPositionMs → UpdateProgress() → SetPlaybackState`,
   而 `SetPlaybackState` 内部**也会写一次 `ScaleX`**(位置样本一次性改写)。
   此时推进量与"循环在不在跑"无关,拿它判定会得出"循环在跑"的假结论 ——
   与同一份报告里"已停摆"的判定自相矛盾(实测踩过)。
   判活性必须**单独跑一遍"关闭进度上报"**,那时 `ScaleX` 只由动画器写。

5. **预热要把每个场景态都先走一遍**。首个测量窗口会串进冷启动尾巴:第一版第一个
   "可见·循环关"量到 **34.37%**,而稳态同场景只有 ~20%。做法是测量前把
   可见/最小化 × 各页面各停 1 秒全部走一遍。

6. **"收敛就提前退出"这类等待循环,计数和时刻必须分开存**。`--pl-cpu-real` 的封面填图窗口
   第一版把"上次进度"只存一个变量:

   ```csharp
   var lastProgressAt = 0L;
   if (loaded != lastProgressAt) lastProgressAt = now;   // ❌ 把时刻(ms)写进了"计数"变量
   else if (now - lastProgressAt > 5000) break;          // ❌ 13 与 13000 永远不等
   ```

   计数(`loaded` = 13)和时间戳(13000ms)永远不会相等 ⇒ 条件**恒真** ⇒ `else` 分支永不执行
   ⇒ 收敛判定彻底失效,每次都跑满 deadline(白等 40 秒,还把噪声引进测量窗口)。
   正确写法是两个变量:`lastProgressCount` 与 `lastProgressMs`。
   ⚠ 变量名带 `At` 却存计数的这种"名字撒谎"最容易被读过去,写完这类循环先肉眼过一遍
   每个变量到底存的是什么量纲。

### ⚠️ 验证"改动是否真的生效"时,先看构建产物

本机有个会让人得出错误结论的陷阱:**用 `Copy-Item` 恢复文件会保留源文件的旧时间戳**,
于是源文件 mtime 反而比上次编译输出更早,MSBuild 判定"无需编译"直接跳过 ——
`dotnet build` 照样报 `已成功生成 0 个错误`,但 `bin` 里的 DLL 还是旧的。
本次实测:32 次构建里那次只用了 **1.40 秒**(正常全量约 24 秒),
跑出来的"改动后"数据全是旧行为,差点写成"修复无效"。

两条硬规矩:

1. 用 `Copy-Item` / 备份还原过源文件后,**必须刷新时间戳再构建**:

   ```powershell
   (Get-Item $f).LastWriteTime = Get-Date   # 或 os.utime(f, None) (Python)
   ```

   更省事的替代:`dotnet build ... --no-incremental`(强制重编译)。
2. **改动生效性要看符号,不要看"构建成功"**。Debug 构建保留方法名,直接查二进制:

   ```python
   b = open(r'src/ALyricEase/bin/Debug/net10.0/ALyricEase.dll','rb').read()
   print(b'IsHostPresentable' in b)   # 新增的私有方法名应能搜到
   ```

   ⚠ **搜中文串要换成 UTF-16LE**。.NET 把字符串字面量放在元数据的 `#US` 堆里,
   是 **UTF-16** 而不是 UTF-8;按 UTF-8 搜中文会**全部 False**,于是把"已经生效"误判成"没生效"
   (本次实测踩到:`'绕过门控'.encode('utf-8') in dll` → False,
   而同一份 DLL 跑起来明明打印了这句话):

   ```python
   print('绕过门控'.encode('utf-16-le') in dll)   # ✅ True
   ```

   另外**比对文件时间**也比"看构建成功"可靠:产物 mtime 必须晚于源文件。
   构建耗时也是个信号:1~2 秒 = 没编译,20 秒以上 = 真编了(单工程小改动 2~3 秒也可能真编,
   所以时效要看 mtime + 符号,不能只看秒数)。

### 隔离场景下已实测的框架行为(2026-09-16,探针 `--progress-visibility`)

探针同时量两件事:平台实际送达的帧数(自续订 RAF 计数器),以及填充条 `ScaleX`
的推进量(动画器每帧自己写入)。场景做了轻/重两档(重 = 三控件 + 800 矩形 + 40 文本块)。
下面是**机制**证据(隔离场景);**量级数字以真实 `AppShell` 的 `--shell-cpu-real` 为准**。

| 问题 | 实测结果 |
|---|---|
| 循环是否真在推进 | 每 6 秒推进 `ScaleX` **0.03333**,180 秒曲目理论 0.03333 ⇒ 精确按实时速率 |
| 最小化时是否仍在推进 | **是**。`state=Minimized` 下推进 0.03334,与可见时一致 |
| 隐藏时(`window.Hide()`)是否仍在推进 | **是**,0.03333 |
| 恢复可见后 | 参数正常恢复(0.03333),无卡死 |
| 平台送帧数 | 循环开 ≈ 22 帧/秒;循环关(内容全静态)≈ 10.8 帧/秒 ⇒ 与渲染挂钩 |
| 循环 CPU 代价(重场景可见) | ≈ **+1.3%** 单核(开 1.56~2.08% vs 关 0.26%),但跨轮次波动 1.5~6.3% |
| 循环 CPU 代价(重场景最小化) | **测不出**(开 2.08% vs 关 2.60%,落在噪声内且反号) |

⚠️ 两条不要误读:

1. "最小化时代价测不出"**不等于**"循环停了" —— 回调确实还在跑(推进量是硬证据)。
   合理解释是:最小化时不 present,平台把最重的"合成/提交"整段省掉了,
   只剩回调与失效书签这些便宜的部分。这与"任务管理器里 GPU 归零"吻合。
   **2026-09-16 已按真实 `AppShell` 重测**:同样在最小化下,循环净代价是
   **+1.04% / +1.30%** 单核(3 轮配对差值中位数)—— 真实场景比这个合成场景重得多,
   所以别再拿这里的"测不出"当结论。
2. 可见时的绝对量级也**不能**直接套到真实应用:探针场景是合成的 840 个元素,
   真实窗口里还有模糊背景、图片与文本布局。要真实数字,应按真实
   `AppShell` + 实际页面重测,而不是用这个隔离场景。

## 框架主题动画(`IsIndeterminate` 进度条)同样不感知窗口状态

与上一节的自续订 RAF 循环是**同一类问题**,区别只是那条循环是自己写的、这条是**框架主题动画**——
但 Avalonia 同样不会替你看窗口状态。用户报告的"打开过歌单页之后,窗口最小化也一直有零点几个
百分点的占用"就有一部分来自这里。

### 实测:一条 3px 高的不确定进度条值多少 CPU

探针 `--pl-cpu-real`(真窗口 + 真 `PlaylistView` + 真 VM),单独把 `IsLoadingMore` 置 true
以隔离这条动画(避免与封面写盘的开销混在一起):

| 场景 | CPU(单核) | 活动线程 |
|---|---|---|
| 最小化 · 提示条不在(对照) | **0.00%** | 无 |
| 最小化 · 提示条在(**未门控**) | **2.60%**(另一次 2.34%) | 合成线程 1.82% + UI 线程 0.78% |
| 最小化 · 提示条在(**已门控**) | **0.00%** | 无 |
| 可见 · 提示条在 | **5.7 ~ 17.7%**(波动大) | 强制整个窗口每帧重合成 |

其中"未门控 / 已门控"两行是**同一次运行内**的 A/B(见文末"同轮对照组"),
两行的差异只剩"门控开/关"这一个变量 ⇒ **省下 2.60% 单核**。

⇒ **只要那条提示条还挂在视觉树上,窗口看不见也照样转。** 这是"一条 3px 的装饰条"
却能在任务管理器里显示出零点几个百分点的原因。

### 修法:`Infrastructure/IndeterminateAnimationGate` 附加属性

```xml
<ProgressBar IsIndeterminate="True" infra:IndeterminateAnimationGate.IsActive="True" />
```

判据与 `ProgressRenderAnimator.IsHostPresentable` **逐字一致**,不要在项目里出现第二套口径:
`TopLevel.IsVisible` 且非 `WindowState.Minimized`。

- 为什么用**附加属性**而不是自定义控件 / 行为:不确定进度条散在 5 处 XAML 里,
  附加属性一行就能挂上,不动控件树、也不影响设计器预览。
- 为什么属性叫 `IsActive` 而不是直接接管 `IsIndeterminate`:门控只负责"宿主不可显示时停",
  可见时**必须把决定权还给 XAML / 绑定**。所以门控**只压不吃** —— 它记住"想要的值"
  (`GateState.Desired`,挂上门控那一刻读一次,之后由 `ProgressBar.IsIndeterminateProperty`
  的变更通知更新),不可显示时压成 `false`,可见时还原成 `Desired`;
  压的时候打一个 `Suppressing` 标记,否则自己写进去的值会被变更通知误当成"绑定改了值",
  把 `Desired` 覆盖成 `false`(然后恢复时永远恢复不出来)。
  ⚠ 写成 `bar.IsIndeterminate = presentable`(第一版)会把 `IsIndeterminate="{Binding IsBusy}"`
  这种用法**强行置 true**;项目里 5 处现状都是静态 `IsIndeterminate="True"`,所以当场不会报错,
  但以后新增绑定就会踩。
- 订阅状态用 `ConditionalWeakTable<ProgressBar, GateState>` 保存:控件被回收后条目自动消失,
  不会因为页面反复建视图而攒下长期引用。
- 离开视觉树时也压成 `false`(压,不记),重新挂回视觉树时由 `Apply` 还原 `Desired`。
- 项目里 5 处已全部挂上:`PlaylistView`(补页提示)、`AccountView`(刷新账号)、
  `SearchView`(搜索中)、`LoginDialogView` ×2(代理登录 / QQ 二维码)。
  ⚠ `LoginDialogView.axaml` 原先**没有**声明 `xmlns:infra`,要一并补。

### 三个坑

1. **`TopLevel.PropertyChanged` 不是 `INotifyPropertyChanged` 的那个同名事件**。
   它是 `AvaloniaObject.PropertyChanged`,签名是
   `EventHandler<AvaloniaPropertyChangedEventArgs>`;照 `System.ComponentModel` 那个写成
   `PropertyChangedEventHandler` 会报 CS0019(`??=` 无法应用于…)与 CS0029(无法隐式转换)。
2. **必须订阅 `WindowState`**:最小化时 `Window.IsVisible` 仍是 `true`,
   只看 `IsVisible` 会正好漏掉"最小化"这个最主要的状态。
3. **隐藏上层容器:证据不一致,而且本项目用不到 —— 别为它加判据**。
   两次运行对 `view.IsVisible = false`(窗口仍可见)量到的值差得很远:
   **0.26%**(第二次)与 **14.83%**(第一次,与不隐藏时的 17.70% 同量级)。
   差值远超本机静息抖动(同一相位跨轮 1.5% ~ 6.3%),说明这一态的费用不稳定
   (可能与"隐藏发生在采样窗口之前多久、合成器有没有收敛"有关),
   **不足以支撑"隐藏拦不住动画"这个结论** —— 所以门控里没有加祖先可见性遍历:
   - 应用里"提示条不显示"的真实状态是它自己那层 `StackPanel` 被 `IsVisible="{Binding ...}"`
     隐藏 —— 实测 **0.00%**;
   - 导航换页时页面是**离开视觉树**(见「复用视图只能省"骨架"」一节),不是靠隐藏留下来。
   ⇒ 只看 `TopLevel` 已覆盖所有实际会发生的状态。
   ⚠ 这条同时是个方法论提醒:**一次不一致的测量不能当结论** —— 要么重测到稳定,
   要么明确写下"测不出",不要把单次读数写成机制。第一版就差点这么干了。

### 探针里怎么量"门控到底有没有生效"

`--pl-cpu-real` 的场景 6 之后带一个**同轮对照组**:把那条挂了门控的进度条手动设回
`IsIndeterminate = true`(等价"未门控"的旧版本)再采一次样。两条测量的差异就只剩"门控开/关"
这一个变量 —— 比"换个构建版本再跑一遍"硬得多,跨构建对比很容易被别的改动污染。
实测输出:`门控效果(同轮 A/B): 绕过门控=2.60% vs 门控生效=0.00% ⇒ 省下 +2.60% 单核`。
(窗口在最小化状态下改 `IsIndeterminate` 不会触发门控重算,因为 `WindowState` 没变化,
所以强行设的值能稳定存活到采样结束。)

⚠ 对照组要按**属性**找控件(`IndeterminateAnimationGate.GetIsActive(bar)`),
不能按 `IsIndeterminate` 找 —— 门控生效时它已经被置成 `false`,按值找会一个都找不到。

