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

## 改集合就会回收行容器 —— "逐位替换元素"照样回收,已解码的图会被丢掉重解一次

**症状**:在歌单页里打开另一个歌单,新歌单的**行封面**一开始就有图(内存里已解码,瞬间上屏),
约 0.5s 后集体闪一下,像又加载了一遍;**歌单头部封面不闪**。

**成因链**:
1. 打开歌单是**两遍装载**:先渲染该页内存快照的行(封面立刻有图),网络 `track overview` 回来后再换在线数据。
2. 旧做法是 `ClearTrackRows()` + 整表重建 ⇒ `ItemsControl` 收到 **Reset** ⇒ **行容器整批离树/复用**。
3. 容器回收时 **DataContext 先被清成 null**,行模板里的 `infra:ManagedCoverImage.Source` 收到 null
   ⇒ 松开那张**已解码**的 `Bitmap` ⇒ 新 DataContext 落下来时再走一遍 **异步** `SetSource`
   ⇒ 中间几帧露出行模板底下的 `ArtFallback` 占位色。

⚠ **最反直觉的一点**:不是只有 `Clear`/`Reset` 才回收容器。
**逐位 `Tracks[i] = newRow`(`NotifyCollectionChangedAction.Replace`)同样会回收。**
实测两档结果一模一样(峰值 6 行缺图、容器身份指纹整批换人),所以"逐位替换集合元素"这个
看起来最保守的做法**并不能**避免闪烁 —— 我一开始就是按这个思路改的,被探针否掉了。

**修法**:让**行对象与集合元素都不动**,只换行内部的播放绑定
(`SongItemViewModel.RebindPlayback`:换 `_playSong`/`_queue`/`_source` + 重算可播性);
可见集合只动"共同 id 前缀"之后的那一段(前缀一个通知都不发,尾部 `RemoveRange`/`AddRange`)。
配套:`RangeObservableCollection.RemoveRange` 用一次 `Remove` 通知摘掉整段尾部,不逐项 `RemoveAt`。

**头部封面为什么天生没事**:它走 `RefreshCover(url)`,**URL 没变就不重载** —— 同一个页面里
"会闪的"和"不闪的"差别就在这一条,拿它当对照能快速定位是不是容器重建。

**量法(`--pl-cover-flash`,真窗口 + 真歌单页 + 真封面,同进程 A/B/C 三档)**:
- 不要量"实化行数" —— 重建前后都等于 6,**看不出来**。
- 要量**每帧"本该有封面却没有"的行数**(`Song.CoverUrl` 非空但 `Image.Source` 为 null)。
- 再加一个**容器身份指纹**(`RuntimeHelpers.GetHashCode` 汇总),用来区分
  "缺图"到底是容器回收还是解码慢 —— 只看缺图数说不清是哪一种。

| 档位 | 峰值缺图 | 缺图帧 | 容器整批换人 |
|---|---|---|---|
| 基线(不动) | 0 | 0/39 | 否 |
| **生产:原地换绑** | **0** | **0/39** | **否** |
| 现状:Clear+Reset | 6 | 2/39 | 是 |
| 候选:逐位替换 | 6 | 2/39 | 是 |

⇒ 只有"集合元素与行对象都不动"这一档全程 0 行缺图。

**连带注意**:原地复用行 = 行上挂的 `Song` 实例**不换**(它是 init-only 的),所以复用只对
"两遍装载的同一批曲目、元数据同源"成立(快照是**纯内存** LRU,与网络结果同一来源);
正因如此 `RebindPlayback` 里要顺手把 `Song.PreferCachedPlayback` 清掉、重算 `IsPlayable`
(离线时按"有没有本地音频"禁掉的行,在线后必须按登录/会员重判,否则会一直灰着)。

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
- ⚠ **同一元素同时命中两条样式时,声明顺序本身就是语义**(没有特异性可以救你,后声明的赢)。
  实例(`LyricView.axaml` 的歌词行):基样式给 detail 的每一行挂了 `BlurEffect Radius=5`,
  `above`/`below1`/`below2` 三条又各自改半径;而设置里的"最佳性能"档要**整档不挂模糊**
  —— `no-blur` 那条只要**写在 `above`/`below1`/`below2` 之后**,就自然压住了它们三档、
  又不动它们的透明度,不需要任何额外写法。写反了会**静默不生效**,症状是"改了样式但像没跑"。
  ⚠ 反过来说:凡是想用"后声明"压住某样式的开关,**它的选择器必须能匹配到同一个元素**
  （这里靠祖先多一个类,而不是给每行写本地值 —— 本地值会盖过样式,以后改样式就全不跟了）。
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

#### 2026-09-17 补测:四态配对(把"在动"与"在树上"分开)

上一版表格把"隐藏"只当成一个状态,于是出现了一个解释不了的矛盾(见下文"坑 3")。
用 `--idle-cpu-real` 的 `ALY_IDLE_DWELL=bar-ab`(同进程、同一份数据、同一次驻留,
四种状态**循环**采样,每态两个 8s 窗口)把它拆开:

| 档 | 提示条实测 | CPU(单核) | 任务管理器口径 | 分配/8s |
|---|---|---|---|---|
| 隐藏 + 强开动画(旧行为) | 被隐藏,**动画=开** | **6.18%** | 0.39% | 8.3MB |
| 可见 + 动画 | 有效可见,动画=开 | **6.38% / 5.80%** | 0.36% / 0.40% | 7.1–7.2MB |
| 可见 + 无动画 | 有效可见,**动画=关** | **0.96% / 0.58%** | 0.06% / 0.04% | 3.1MB |
| 隐藏(真实应用做法:只翻 `IsLoadingMore`) | 被隐藏,动画=关 | **0.00% / 0.39%** | 0.00% / 0.02% | 3.1MB |

三条结论:

1. **成本在"动画在转",不在"控件在树上"** —— 同为隐藏态:动画开 6.18% vs 动画关 0.39%;
   同为可见态:动画开 6.38% vs 动画关 0.96%。`分配/8s` 是同一指纹(动画开 7~8MB,
   动画关 3.1MB),等于把"每帧都在分配"这件事直接看到了。
2. **框架动画一旦跑起来过,`IsVisible=false` 停不了它** —— 隐藏 + 动画开仍是 6.18%,
   与可见 + 动画同量级。⇒ "不可见就不产生成本"**只在该控件从未实化过时成立**。
   用户报的"打开过歌单页之后什么都不做也一直有占用"正是这条:提示条只要出现过一次,
   之后即使已经隐藏也继续空转,直到页面**离开视觉树**才归零。
3. 门控补上"控件自己是否被有效显示"之后，只翻业务标志的隐藏态 **6.18% → 0.00%/0.39%**，
   而可见态照常转（5.80%~6.38%）⇒ 只压空转，不动业务语义。
   最硬的一对（`ALY_IDLE_DWELL=bar-onoff`：**可见+动画 → 只翻业务标志隐藏**，
   后者完全不碰 `IsIndeterminate`，所以"动画=关"只可能是门控写的）跑了 4 对，全部一致：
   `8.31→0.58`、`4.64→0.39`、`5.41→0.19`、`2.52→0.19`（单核%），
   分配同步从 7.1MB/8s 掉到 3.1MB/8s。

### 修法:`Infrastructure/IndeterminateAnimationGate` 附加属性

```xml
<ProgressBar IsIndeterminate="True" infra:IndeterminateAnimationGate.IsActive="True" />
```

判据 = `ProgressRenderAnimator.IsHostPresentable`(宿主)**且** `bar.IsEffectivelyVisible`(控件自己)。
宿主那条不要在项目里出现第二套口径:`TopLevel.IsVisible` 且非 `WindowState.Minimized`。
控件那条是 2026-09-17 补的(见上文四态配对):**只看宿主会漏掉"提示条自己已经隐藏但动画还在转"**。

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
- **"控件自己有没有被显示"怎么订阅**(2026-09-17):`Visual.IsEffectivelyVisibleProperty`
  在 Avalonia 12 **不是公开成员**(只有 `IsEffectivelyVisible` getter),没法直接订阅;
  只能**把自身 + 全部可见祖先的 `IsVisibleProperty` 都订上**(应用里的实际做法是把外层
  `StackPanel` 的 `IsVisible` 绑成业务标志,控件自己的 `IsVisible` 一直是 `true`)。
  ⚠ 订阅集必须**显式记住**(`GateState.Watched`),在 `DetachedFromVisualTree` 里逐个解开:
  那时祖先链已经断了,靠 `GetVisualParent()` 回走收不回来;而祖先里的壳层/窗口比页面活得久,
  漏解就是真实泄漏(处理器是静态 lambda 捕获了控件)。
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
3. **隐藏上层容器:第一版判成"证据不一致、用不到",是错的 —— 2026-09-17 已定案**。
   当时的两次运行对 `view.IsVisible = false`(窗口仍可见)量到 **0.26%** 与 **14.83%**,
   于是写下"证据不一致,不足以支撑'隐藏拦不住动画'"并决定不加祖先可见性判据。
   四态配对(上文)把这两次读数解释清了:**它们量的是两个不同状态** ——
   0.26% 那次该控件**从未实化过**(动画压根没启动过),14.83% 那次**动画已经跑起来过**。
   缺的自变量是"动画是否启动过",不是"测量不稳定"。
   ⇒ 结论反过来了:**动画启动过之后,隐藏上层容器拦不住它**,门控必须看控件自己的有效可见性。
   ⚠ 方法论:**"两次读数不一致"时不要急着判成噪声** —— 先找缺掉的自变量(这里就是
   "这个控件此前动过没有");配对设计(同进程、同数据、循环切换单一变量)比"多测几次求平均"
   有用得多。

### 探针里怎么量"门控到底有没有生效"

`--pl-cpu-real` 的场景 6 之后带一个**同轮对照组**:把那条挂了门控的进度条手动设回
`IsIndeterminate = true`(等价"未门控"的旧版本)再采一次样。两条测量的差异就只剩"门控开/关"
这一个变量 —— 比"换个构建版本再跑一遍"硬得多,跨构建对比很容易被别的改动污染。
实测输出:`门控效果(同轮 A/B): 绕过门控=2.60% vs 门控生效=0.00% ⇒ 省下 +2.60% 单核`。
(窗口在最小化状态下改 `IsIndeterminate` 不会触发门控重算,因为 `WindowState` 没变化,
所以强行设的值能稳定存活到采样结束。)

⚠ 对照组要按**属性**找控件(`IndeterminateAnimationGate.GetIsActive(bar)`),
不能按 `IsIndeterminate` 找 —— 门控生效时它已经被置成 `false`,按值找会一个都找不到。

## 布局与外观一律走 XAML:别在 C# 里建视觉树,也别写"局部值"

**规则**:能用 XAML 表达的布局与外观(样式、ControlTheme、DataTemplate、容器查询)就不要在 C# 里
`new` 控件、也不要给它设属性。**更关键的推论**:凡是"以后可能被样式/主题/状态覆盖"的属性,
都不要直接写在控件上(`Width="120"`、`FontSize="14"`、`Margin="..."`、代码里的 `FontSize = 14` …),
而要提炼成 `Classes` + `Style`。

**为什么**(官方 `BindingPriority`,数值越小优先级越高 —— 2026-09-17 复核文档):

| 优先级 | 来源 | 什么时候产生 |
|---|---|---|
| 1(最高) | `Animation` | 动画 / 过渡写入的值,**连局部值都能盖** |
| 2 | `LocalValue` | **XAML 里直写的属性**、`new Control { X = … }`、代码 `SetValue`、顶层 XAML 里直写的绑定 |
| 3 | `StyleTrigger` | **带条件激活**的选择器的 Setter:伪类(`:pointerover`/`:pressed`)、样式类、子位置、属性匹配 |
| 4 | `Template` | `ControlTemplate` 内直写 |
| 5 | `Style` | 无条件激活的 Setter —— ⚠ **比 `Template` 还低** |
| 6 | `Inherited` | 从父级继承 |
| 7(最低) | `Unset` | 用属性注册时的默认值 |

⇒ 只要留下一个局部值(不管来自 XAML 还是 C#),后面的样式、主题、伪类样式、容器查询**全部静默失效**,
而且**不报错、不告警**(选择器匹配不上时也不会提示)。官方原话:
"If you want styles to be able to override a property, avoid setting it directly in XAML."

另外三条容易踩的:
- `{StaticResource}` / `{DynamicResource}` **不改变优先级**(资源标记扩展对优先级没有影响),
  所以 `Width="{StaticResource CardWidth}"` 一样是局部值、一样挡样式。
- **Avalonia 没有 CSS 的 Specificity**。同级之间的次序是:先比"视觉树局部性"(离控件越近的样式越优先),
  再比 `Styles` 集合里的书写顺序(**后写的赢**)。
- **控件名选择器(`#name`)不是条件性的** ⇒ 它落 `Style`(5)这一层,因此靠它盖不住
  `ControlTemplate` 里的直写值(4)。要盖模板里的值,选择器得能带到 `/template/` 内部。

**该用什么替代**:

| 想做的事 | 不要 | 应该 |
|---|---|---|
| 变体外观(尺寸/间距/圆角) | 每个控件直写属性 | `Style Selector="Button.card"` + `Classes="card"` |
| 状态(悬停/按下/选中) | 代码里订阅指针事件改属性 | `Style Selector="Button:pointerover"` |
| 整套换皮 | 到处改属性 | `ControlTheme`(可按类型整体替换/移除) |
| 随容器尺寸变化 | 订阅 `SizeChanged` 在代码里算断点 | 容器查询(见下) |
| 平台 / 形态差异 | `if (OperatingSystem.IsAndroid())` 改布局 | `OnFormFactor` / `OnPlatform` 标记扩展(启动时解析一次) |
| 颜色 / 字号线 | 硬编码色值 | `DynamicResource` + 主题字典 |
| 列表行外观 | 代码里造行控件 | `DataTemplate` / `ItemTemplate` |

### 容器查询(Avalonia 11.3+,本项目 12.1.1;仓库里已在两处用上)

把**祖先**声明成容器,查询写在**祖先**的 `Styles` 里(不是写在容器自己身上)。
项目内的两个落地实例:

- `Views/AccountView.axaml`:容器 = 外层 `ScrollViewer`(`Container.Name="accountHost"`,
  `Container.Sizing="Width"`),`max-width:641` 时把两个登录按钮从"并排"改成"纵向全宽"。
- `Views/LoginDialogView.axaml`:容器 = 根 `Grid`(`loginRoot`),`max-width:760` 时把两栏布局
  折成"方法列表横排置顶 + 内容落到下方"。

```xml
<ScrollViewer Container.Name="accountHost" Container.Sizing="Width">
  <ScrollViewer.Styles>
    <ContainerQuery Name="accountHost" Query="max-width:641">
      <Style Selector="Button#NetEaseLoginButton">
        <Setter Property="Grid.Row" Value="1" />
        <Setter Property="Grid.ColumnSpan" Value="2" />
        <Setter Property="HorizontalAlignment" Value="Stretch" />
      </Style>
    </ContainerQuery>
  </ScrollViewer.Styles>
  …
</ScrollViewer>
```

- `Container.Name` 只是按名字挂钩,**不要求全局唯一**,同名容器会一起响应同一条查询。
- `Container.Sizing`:`Normal`(默认,不被查询)/ `Width` / `Height` / `WidthAndHeight`。
  ⚠ 选 `Width`/`Height` 时容器会**吃掉父级允许的最大尺寸**(DesiredSize 直接用约束值),
  随手加在不该撑满的控件上会改变布局。项目里两处都挂在**本来就该撑满**的元素上
  (`ScrollViewer` / 根 `Grid`)—— 照这个来,别挂到卡片之类会被撑变形的控件上。
- 条件:`min-width` / `max-width` / `width` / 同名 height 系列;`,` = 或,`and` = 且。
- 两条硬限制:**① 不能写在 `Style` 里**(只能直接当 `Styles` 的子元素,或写在 `ControlTheme` 的
  `Styles` 里);**② 查询内的样式不能作用于容器自身及其祖先** —— 否则尺寸互相触发会来回抖。
- 把 `TopLevel` 当容器时,行为等价于媒体查询(响应窗口尺寸)。

**项目定稿的两条补充硬规则**(2026-09-16,踩过才写下来的):

- **参与断点切换的控件不许写 `Grid.Column/Row` 局部值**。断点要改的是"列 + 行"这一组,
  而局部值优先级(0)高于 `Style`(3):只被查询改到的那一半会生效,另一半被局部值钉死,
  表现是**布局"改了一半"**(行生效、列失效)。要么两处都交给样式,要么整体换容器。
- **`Grid.ColumnDefinitions` / `RowDefinitions` 在 Avalonia 12 里已经不是 `AvaloniaProperty`,
  不能进 `Style` 的 `Setter`** —— 报 `AVLN3000`,而且**增量构建可能假成功**,清 `obj` 后必炸。
  所以"两栏 ⇄ 单栏"的断点切换不要试图改列定义,改用 `DockPanel`(切 `Dock` + `Width=NaN`)。
  排查时若遇到"错误只在清构建缓存后出现",先怀疑这条。

### 必须从 C# 写时的三个出口

- `SetCurrentValue(prop, value)`:写在**当前生效的那一层**,不新建 `LocalValue` 条目 ⇒
  既更新了值,又不会把样式/绑定永久顶掉(控件实现里更新自己的属性就该用它)。
- `SetValue(prop, value, BindingPriority.Style)`:真要指定层级时用(一般只有样式系统内部需要)。
- `ClearValue(prop)`:撤掉自己写的局部值,让下层来源重新生效。

项目内的范例与反例:`Infrastructure/IndeterminateAnimationGate` 必须**临时压过**绑定
(最小化时强停动画),所以它不能只靠 `SetCurrentValue` —— 它是"直接赋值 + 自己记住绑定想要的值
(`Desired`)+ 压制期间打 `Suppressing` 标记",否则自己写的值会被变更通知当成"绑定改了值"而吃掉。
这是"确实必须从代码改"的正确写法。
反例:`Views/PlayerBarView.axaml.cs` 里用 C# 造 `MenuFlyout` 的菜单项、并给图标 `TextBlock`
写 `FontSize = 14` / `VerticalAlignment` —— 想统一改菜单图标字号就得改代码、也进不了主题。
(真要修,得先确认 Popup 里的内容吃哪一层的 `Styles` —— 这一条**还没实测过**,别想当然。)

### 例外:可以理直气壮在 C# 里建视觉树的两处

- **探针 / 测试夹具**:`src/ALyricEase.Headless` 里 30+ 处 `new Grid/StackPanel/…` 是有意的
  (要合成一个受控场景,甚至故意堆 800 个元素量成本)。但要记住这些夹具**同样享受不到样式** ——
  所以"探针里量到的观感/量级"不能直接当真实应用的数字(见前面"隔离场景"那一节的同类提醒)。
- **离屏渲染**:`Views/AlbumCoverBackground.axaml.cs` 的 `ExtractPalette` 造一个 48×48 `Image`
  只为渲进 `RenderTargetBitmap` 取色 —— 它不进视觉树、没有样式语义,不算"布局"。

### 现状盘点(2026-09-17)

`src/ALyricEase`(应用本体,不含探针)里"在 C# 里建布局"只剩 2 处,其中 1 处属上面的例外:
`Views/AlbumCoverBackground.axaml.cs`(离屏取色,例外)与 `Views/PlayerBarView.axaml.cs`
(菜单项,见上)。**新增代码时别让这个数字涨**;排查"为什么样式改了没反应"时,先找有没有局部值。

## GPU 也要归因:全屏组合动画在你没看它的时候照样烧钱

**现象**:用户报"播放详情页 GPU 占用 6%,有点高"。任务管理器里进程 GPU 一列常年有值,
但 CPU 读数挺好看 —— 这类代价落在**合成与光栅化**上,CPU 计数器量不到,得换个尺子。

**工具**:Windows PDH 性能计数器 `\GPU Engine(*)\Utilization Percentage`
(`src/ALyricEase.Headless/GpuUsageSampler.cs`,P/Invoke `PdhOpenQueryW` /
`PdhAddEnglishCounterW` / `PdhCollectQueryData` / `PdhGetFormattedCounterArrayW`)。四条硬约束:

- 按 **`pid_` 前缀**过滤实例名(计数器给每个进程的每个引擎各一条实例,不加前缀会把整机算进来)。
- **加总所有引擎**(3D / Copy / Compute / VideoDecode)。只看 3D 会低估 —— 组合动画的代价经常
  落在别的引擎上。同时留一个"单引擎峰"口径,任务管理器那一列更接近它。
- **丢掉第一个采样**:速率型计数器要两次收集才有增量。
- **窗口必须可见且没被遮挡**。被遮挡时 DWM 不合成,我们的帧没提交,GPU 恒为 0 ——
  这种"数字很漂亮"的错误比崩溃更难查。所以探针强制 `Topmost = true`,并明说会盖住桌面约两分钟。

**方法**:同轮消融矩阵。每个场景写成一份**完整**状态(不是"在上一个场景上再关一样"),
行与行才能任意相减;同轮而非跨构建,差里只剩被消融的那一项。再插一行同状态复测当噪声底。

**实测**(`--np-gpu-real`,1200×720,`AlbumCoverBackground` 那 10 个全屏径向渐变椭圆):

| 场景 | GPU | CPU |
|---|---|---|
| 首页(详情页关),门控生效 | **0.95%** | 1.8% |
| 首页(详情页关),强制让漂移继续跑 | 8.16% | 22.5% |
| 详情页·默认 | **8.43%** | 22.0% |
| 详情页·关掉「动态背景」开关 | **0.93%** | 3.6% |
| 详情页·色团层隐藏 | 6.24% | 19.9% |
| 详情页·停两个进度 RAF 循环 | 8.34% | 25.2% |
| 详情页·上面全关 | 0.87% | 2.7% |
| 歌词面板·默认 | 17.46% | 46.0% |
| 歌词面板·逐行不用 `BlurEffect` | 9.69% | 28.3% |

⇒ **"让它一直动"值 7.50%,图层"画出来"只值 2.20%,两条自续订 RAF 循环 0.09%(≈0)。**

**根因**:`AlbumCoverBackground` 由播放详情页覆盖层承载,而覆盖层在真实 `MainWindow` 里是
**常驻**的 —— 只靠 `RenderTransform` 移出窗外,视觉树上一直都在。于是色团从应用启动起就在漂移,
哪怕详情页根本没打开、哪怕整层移在窗外看不见。横跨整窗的组合动画会让合成器**每帧把整幅窗口
重画一遍**,代价与"看不看得见"完全无关。**钱花在动上,不在画上。**

**修法**:控件加 `MotionEnabled`(默认 `true` 保住设计原意),宿主按
`ShowNowPlaying && AppState.DynamicBackground` 绑定。两个要点:

- 停是**隐式**的(任一门控条件不满足就停),但**重新开始必须显式** —— `StopMotion()` 要复位
  私有 `_motionStarted` 并 bump `_motionGeneration`,否则"关掉再打开"会变成单向开关。
  与 `ProgressRenderAnimator` 同一套约定。
- 原版 LyricEase 的 `SettingsView` 里本来就有「启用播放详情界面的动态背景效果」(`DynaBackEnableCheckBox`),
  做法一致 —— 让用户能关掉它是原版就有的设计,不是我们加的。

**歌词面板那一档**(顺带量出来的):面板本身 +9.0% GPU;其中逐行 `BlurEffect` 净代价 **+7.77%**,
只模糊当前句上下两行的写法是 **+6.09%** —— 收益小、观感却变了,所以**没做**。
(模糊确实渲染出来了,见下面抓屏判据;该谈的是换个便宜画法,不是能不能删。)

### ⚠️ "让它动得更省"没有捷径:降频就是拿流畅度换钱,而 UI 线程定时器根本快不起来

(2026-09-21)门控只解决"看不见时别动"。**"看得见时按多快的频率动"是另一笔账** ——
试过把漂移从组合动画改成 33ms `DispatcherTimer` 自己推帧:**读数很漂亮、观感直接判负**,已回退。

| 驱动方式(1920×1080,同轮拉丁方) | GPU | CPU(单核) | 实测更新率 |
|---|---|---|---|
| 20 条 `Vector3DKeyFrameAnimation`(合成器推帧,**生产现状**) | **7.5~12.1%** | 20~37% | 按显示刷新率 |
| 33ms `DispatcherTimer` 写 `visual.Translation/Scale`(已否决) | **0.9~2.3%** | 3.6~5.7% | **12.5~14 次/秒** |
| 漂移整个关掉 | 0.5~1.6% | 1.0~4.2% | — |
| 详情页主画布整块不画(漂移照开) | +0.11% | — | — |
| 色团层整块不画(漂移已停) | +0.85% | — | — |

**成本公式**:代价 ≈ **出帧次数 × 每帧固定开销**,与"每帧画什么"几乎无关 ——
内容侧两个锚(0.11% / 0.85%)加起来还不到频率那一档的零头。换算到每帧:组合动画
≈ **0.16% GPU/帧**、定时器 ≈ **0.13% GPU/帧**,几乎一样 ⇒ "换定时器省下的"全在"帧少"上,
而帧少就是肉眼看到的卡。⚠ 所以**别把"读数变漂亮"当成"优化成功"** ——
少画几帧谁都能省,那不是性能工作,那是换了个更差的效果。

三条必须记的:

- **`DispatcherTimer` 的"名义间隔"不是实际频率**，而且它在这台机器上有个 ≈14 次/秒的**上限**:
  33ms 名义实测只有 **12.5~14 次/秒**；改成 16ms **读数一模一样**；优先级从 `Render` 换成
  `Normal` 也还是 **13.9 次/秒**（`ALY_NP_DRIFT_PRIORITY` 就是为这个加的）。
  **既不是间隔也不是优先级卡着它** —— ✅ **机制已结案,见本文末尾《UI 线程静息时长睡 ~79ms…》**:
  静息时 UI 线程在长睡,只有"投递的消息"能打断它。但要报"这条驱动多快"，必须数出
  "它真的推进了几次"；生产侧那个只读计数器已随回退删掉，探针里由被否决档的复刻承担。
- **UI 线程驱动的相位别取 `Environment.TickCount64`**:它分辨率 15.6ms,低帧率下位置会被
  量化成阶梯;真要靠 UI 线程推,用 `Stopwatch`(高分辨率)才谈得上均匀。
- **两套机制的门控义务和开关是反的,换驱动必须成对处理**:
  组合动画 `IsVisible=false` / 最小化都停不了它,但它**不渲染即 0 开销**(所以老实现不需要
  `IsHostPresentable()`);定时器在最小化时照样跑(且 `Window.IsVisible` 最小化时仍是 `true`),
  换过去就**必须**加那道门控。反过来 `StopAnimation("Translation"/"Scale")` **停不掉**定时器,
  停定时器也**不能**靠 `StopAnimation`。

⚠ 别把这条外推成"纹理/缓存能省":烘成 1000² 纹理 + 单视觉纯平移(原版在 DWM 里的模型)
在**同一个定时器**下是 **11.35% vs 1.64%**,反而贵 7 倍。详见 `perf-notes.md` 第二节。



### ⚠️ 会"每帧在变"的控件不能挂 `Effect`:半径写 0 也一样贵

(2026-09-21)上一条当时判成"收益小、不值得",**漏掉了真正的自变量**。按"当前句 / 其余行"重切一刀:

| 挂在谁身上(1200×720,同轮消融) | GPU |
|---|---|
| 当前句那行(`ListBoxItem:selected`),`BlurEffect Radius="0"` | **14.86%** |
| 当前句那行,`BlurEffect Radius="1.5"`(真模糊) | **15.34%** |
| 当前句那行,**没有 Effect** | **6.93%** |
| 其余 4 行(above/below1/below2,半径 1~2.5) | 只值 +1.27% |

⇒ **半径 0 和真模糊一样贵** —— 贵的不是半径,是"这一行"。修法:`LyricView.axaml` 的
`:selected` 样式从 `<BlurEffect Radius="0"/>` 改成 `<Setter Property="Effect" Value="{x:Null}" />`
(必须**显式 null** 才压得住基样式那条 `Radius="5"`;空 Setter 压不住)。省 **+7.92% GPU /
22 点单核 CPU**(14.86% → 6.93%)、跨三次独立运行复现;抓屏像素差 **0.00%**(与噪声底同档
⇒ 视觉零变化,半径 0 本就是恒等变换)。

**为什么**:当前句是全场唯一**每帧都在变**的那一行(字号最大、被滚动定位、带缩放过渡)。
`Effect` 非 null 就强制该控件走"离屏层 + 图像过滤",而这一层每帧失效 ⇒ **每帧重建一次离屏**;
其余行内容静止、层可以复用,所以便宜 6 倍。旁证:把色团漂移停掉后,整块歌词面板
(含那 4 行模糊)只值 **+0.06%** —— 代价是"每帧重画"与"每帧重建离屏层"的**乘积**,
不是模糊本身。

**可复用判据**:给控件挂 `Effect` / `OpacityMask` / 任何触发离屏的东西之前,先问一句
"它会不会每帧变"。会变的(动画主体、自动滚动定位的当前项、跟着进度走的元素)就别挂 ——
哪怕参数写的是 0、透明、"无效果"。

### 写这类 GPU 探针踩到的坑(都实测过)

- **验收场景必须"一行代码都不碰"。** 第一版在默认档里读回控件的 `MotionEnabled` 再"推"一遍动画
  状态,结果造出"探针 `StartAnimation` 与应用 `StopAnimation` 挤在同一帧"的时序 —— **这种时序停不掉**,
  于是量出"设置没用"(8.42%,与不关开关一样)。改成完全不碰之后,同一状态是 0.93%。
  教训:探针自己去拨开关的话,应用改没改好都会量出同一个数。
- **消融行会污染它之后的所有行,而且是静默的。** 绕过控件直接对合成视觉 `StopAnimation`,
  控件里 `_motionStarted` 仍是 `true` 而动画已停;后面的默认行属性没变化 ⇒ 没有变更通知
  ⇒ 永远不会重新点火。实测整张表从那一行起全掉到 0.7~1.0%(本该 8.5%),**而消融行自己看着完全正常**。
  根因:`StopAnimation` 只有在**控件自己的 `StopMotion` 里**才会 bump 那个代次,而代次正是用来
  作废在途启动的。结论是别要这种东西 —— 它想量的那一档改由"用户真按设置开关"给出,更硬也更少坑。
- **`CompositionVisual.Translation` 的 getter 读的是基础值**,不反映组合动画推到服务端的当前值
  (实测 13 个场景全是"位移 0.00px",连明确在跑的默认态也是)—— 拿它判"还在不在动"是瞎的。
- **抓屏像素差也不适合判它**:漂移按设计就慢到每帧亚像素、又是柔和渐变,带阈值的像素差恒为
  `0.00%`(强制开漂移那一档也是)。这反过来是个有用的结论 —— 它的代价是"每帧重画整窗",
  不是"每帧画面都在明显变"。同一套抓屏在**歌词模糊**上是好用的(噪声底 0.00% / 消融差 5.24%)。
- **"帧节奏"那一列不能当判据**:同一个状态跨轮读到 7.0ms 和 76.4ms(143fps ↔ 14fps),纯噪声。
- **夹具里的控件引用会失效。** 歌词面板收起再展开会**重建 `ListBoxItem`**,沿用过一次的快照数组
  会指向已脱离视觉树的旧实例 —— `Effect` 写了、画面纹丝不动。实测同一套判据前四轮都给出 4.58%,
  有一轮 0.00%,并据此打印出"模糊没渲染 ⇒ 纯白付、删掉零损失"这个**很强的错误结论**。
  两条修法:① 夹具每次从视觉树重取当前实化的控件,不要缓存快照;
  ② 要保留"原本的值"只能从**活的、样式已生效**的控件上读 —— 样式里每条
  `<Setter Property="Effect"><BlurEffect Radius="…"/></Setter>` 会给**每一行各造一个**实例
  (半径还不同:远景 5 / above 1.5 / below1 1 / below2 2.5),不能共用一个。
  另外把"恰好 0.00%"单列一档,写明"先怀疑夹具",别让它冒充"特性无效"的证据。
- **`CacheMode="BitmapCache"` 会冻结 `Effect` 的后续变化(2026-09-21)**。它是官方文档推荐的
  "昂贵但少变化的视觉"的正解,也确实省 —— 给歌词行挂上,同轮 A/B 省 **2.97~4.46% GPU**
  (1920×1080,摘掉那一档 16.55% vs 带上 13.43%)。**但**挂上之后再改 `Effect` **不再反映到画面**:
  把每行 `Effect` 置空后抓屏像素差 **0.00%**(没挂缓存时同一操作是 5.68%)⇒ 缓存把 Effect 的变化吞了。
  歌词行的模糊正是靠 class 切换(`above`/`below1`/`below2`)驱动的,用它会把模糊状态
  冻在第一次栅格化那一刻 ⇒ **撤回,不采用**。教训:缓存类优化先问"这个视觉的属性之后还会不会变";
  会变就别缓存,或者先证明那条变化路径能触发缓存重建。
- **`BitmapCache` 挂在"组合动画 + 大面积填充"的视觉上没用**。给色团那 10 个全屏径向渐变椭圆
  各挂一个,只省 **+0.15~0.53%**(噪声内)。它的代价不在"渐变计算",而在
  "**全屏填充率 × 10 层半透明混合**" —— 栅格化之后照样要全屏填一遍、照样要混合 ⇒ 缓存无从省起。
- **同理,把径向渐变换成"预渲染位图填充"也没用(2026-09-21)**。做法是离线渲染一张 256² 的
  `RenderTargetBitmap`,用它当 `ImageBrush` 替掉 `RadialGradientBrush`,每个椭圆一张。
  位置平衡 A/B 两次独立运行都读成"无差异"(+0.23% / +0.32%,都落在组内极差里),抓屏平均通道差
  **0.49 级**(对照:改成纯色是 6.23 级 ⇒ 判据足够灵敏)。即"渐变求值"根本不是开销项。
  ⚠ 这两档在设计上要写明:**单次填充画法的改动,收益上限 = 整个色团层不画的总量**,先把这个
  "收益上界"锚出来,再决定值不值得为它加代码(本项目一个椭圆换一张位图要多 ~2.6MB 纹理 + 每首歌栅格化一次)。
- **⚠ 消融变体和"轮内位置"绑在一起,会伪造出一个稳定的假差值。** 同一处改动在两个方向上
  各量到过一次"显著"(一次 -2.81% 像大赚,反过来的设计里 +1.39% 像变贵),根因是**夹具的第 1 行
  系统性比第 2 行高 ~1.4%**(GPU 计数器读的是引擎忙碌时间占比,采样窗口里的外部负载会整体平移读数)。
  修法:**位置平衡交错** —— 每轮按 `A B A B` / `B A B A` 交替,A 与 B 各占一半轮次的首位。
  一平衡,那个 2.81% 立刻塌成 0.23%。配套两条:① 报告里**必须给组内极差**,只给均值就没法判
  "差值 < 极差 = 噪声";② **别跨轮比较绝对值**,外部负载会让同一构建在两轮里读出 8.7% 和 12.0%。
- **跑在整轮末尾的那几行帧节奏会掉到 13~14fps(70ms+)**,连带 GPU 读数失真(实测末尾那行读到
  4.68%,而同轮前面稳定在 8.7%)。锚"收益上界"的那一档**别放在末尾**,或者干脆多用几轮取交集。
- **官方性能文档明说的两条**:① `BlurEffect` / `DropShadowEffect` 在 Skia 里"计算昂贵",
  复杂 UI 开模糊能把 60fps 打到 30fps 或更低;② 每个 `BoxShadow` 各加一个独立 render pass。
  另外 `SkiaOptions.MaxGpuResourceSizeBytes` 默认只有 **约 28MB**,超了就把纹素每帧重新上传;
  而 `CompositionOptions.UseRegionDirtyRectClipping` **从 12.1 起默认关闭**且明说会增加 CPU 开销,
  只对无 GPU 加速的平台有意义 ⇒ **不要开**。

## 新增内容页:内容根 `margin bottom` = `PlayerBarReserve`(每个断点都要留)

**背景**:底部播放条(`PlayerBarView`)在 `AppShell` 里以 `VerticalAlignment="Bottom"` +
`ZIndex=1` **覆盖**在内容区上方(常驻显示、不随"有无曲目"隐藏),`PlayerBarHeight=100`,
顶部进度条还向上凸 14px(落在预留带内,不遮内容)。这是 overlay 语义——滚到中途内容从播放条
下方穿过(亚克力背景透出模糊内容),只有滚到底才空出一片。所以**不能**把播放条改成 docked
布局,只能让每个内容页在底部留一段等于播放条高度的空白。

**规则**(两头都要):

1. 根 `ScrollViewer` 加 `Classes="page-scroll"` —— 它现在**只**负责"把本页自己的纵向滚动条
   抬起 `PlayerBarReserve`"(见下一条);
2. **内容根**(内容树里直接挂在滚动视口下、包住全部内容的那一个元素,如
   `StackPanel#PageContent` / `Grid#PageLayout` / `StackPanel.results-body` /
   `Grid.detail-shell`)写 `Margin` 底边 = `PlayerBarReserve`(=120,
   `Styles/Foundation/Dimensions.axaml`,与 `PlayerBarHeight` 对齐)。

⚠ **预留不能再走 `ScrollViewer.Padding`**(2026-09-25 `eec4b92` 已废弃该方案):padding 会压缩
视口,内容被永久约束在播放条上方、**根本进不了播放条(亚克力)后面的区域**,"覆盖式"不成立
(实测亚克力失效、内容被硬切在播放条上缘)。改成内容根 margin —— margin **计入滚动范围**,
滚到底才空出,最后一行正好抬到播放条上方。

```xml
<ScrollViewer Classes="page-scroll" VerticalScrollBarVisibility="Auto" ...>
  <StackPanel Margin="48,0,32,120">   <!-- 底边 = PlayerBarReserve,每个断点都要留 -->
</ScrollViewer>
```

`page-scroll` 实际只剩一件事(`Styles/Controls/Scrolling.axaml`,两端 `App.axaml` 都已
`StyleInclude` 该文件,自动生效):

- 给**它自己的纵向滚动条**设底部留白(`Margin="0,0,0,PlayerBarReserve"`,选择器为
  `ScrollViewer.page-scroll /template/ ScrollBar:vertical`),滑块/轨道不伸到播放条后面
  (保留"滚到中途轨道在播放条上方"的观感)。
  ⚠ **这里绝不能写成后代式 `ScrollViewer.page-scroll ScrollBar`** —— 空格是后代选择器、
  不区分层级,会把**页内嵌套滚动容器**的滚动条一起命中:个性推荐里横向卡片区的水平滚动条
  被套上底部 100px 边距,直接被顶到容器中间(2026-09-17 实测回归)。
  `/template/` 只命中该 ScrollViewer 自己模板里的滚动条,不会进嵌套 ScrollViewer;
  `:vertical` 再排除横向条(根内容页横向滚动均为 Disabled,这里只为防误伤)。
  回归线:`--pagescroll`(`src/ALyricEase.Headless/PageScrollReserveProbe.cs`),
  用最小夹具断言"页根纵向条抬起 `PlayerBarReserve`、嵌套容器的横/纵条原样",
  并已做受控 A/B 确认它真能测到后代式写法(后代式 → 5 条不符、exit 1)。
  ⚠ 期望值必须从 `PlayerBarReserve` 资源读,**不要写死数字**:它曾经写死 100,`eec4b92` 把资源
  改成 120 后这条线就一直红着、没人发现(2026-10-03 才发现并修)。

### ⚠ 预留"每个断点都要留",且小心局部值把断点覆盖变成死码

内容根的 margin 写在**样式**里,于是会被 `.compact` / `.narrow` 的覆盖整份替换掉 —— 覆盖时
只改左右、忘了底边就会吃掉预留(而且只有那个断点出问题,宽屏自测完全看不出来):

| 页面 | 基础 / `.compact` | `.narrow` | 结果 |
|---|---|---|---|
| `RecommendView` | 120 / 120 | **0**(`eec4b92` 起) | ❌ 窄窗口滚到底最后一行压在播放条下(2026-10-03 用户报"个性推荐的自动留白失效了") |
| `SearchView` | 120 | 8 | 覆盖是**死码**(见下),当前不生效 |
| `RecentPlaybackView` | 120 | 0 | 覆盖是**死码**,当前不生效 |

窄窗口(窗口宽 < 641,即 `narrow` 档;`MainWindow.MinWidth=360`,桌面也能拖到)是唯一暴露路径
—— `eec4b92` 逐页提预留时只提了基础(和部分 `.compact`)margin,漏了 `.narrow`。

**为什么只有推荐页真的坏了**:Avalonia 里**局部值(代码/内联属性)优先级高于样式 setter**。
`RecentPlaybackView` 的 `Grid#PageLayout`、`SearchView` 的 `StackPanel.results-body` 都把 `Margin`
写成了内联值(`48,20,32,120` / `24,12,24,120`),它们那两条 `.narrow` 覆盖**根本不生效**
(实测三档量出来都是 120)——这既是巧合也是坑:谁哪天按本仓"可能被样式覆盖的属性不写局部值"
的约定把内联值摘掉让覆盖生效,底边写 0 / 8 就会立刻吃掉小屏预留。所以那两处死码的底边也一并
改成 120(零行为变化,只是拆雷)。

**回归线**:`--pagereserve`(`src/ALyricEase.Headless/PageBottomReserveProbe.cs`)——
真实 View + 真实 VM(塞够内容让页面能滚)+ 假播放条 overlay(`ZIndex=1`、高 100),
3 个页面 × 3 个断点各跑一次,滚到底后在窗口坐标量
`视口下沿 − 预留承载元素下沿`,必须等于 `PlayerBarReserve.Bottom`;同时断言内容真的撑满视口
(否则这轮根本测不出预留,不能算通过),并把假播放条高也纳入判定(留白 < 100 就是"被遮住")。
每例落一张滚到底的截图,失败时直接看得到现场。

**为什么集中**:之前各页各写一个不一致的硬编码底部留白(`Settings` 只留 40、`Search` 留 90、
其余 110/116),统一后改播放条高度只动 `Dimensions.axaml` 一处;但"每页 × 每断点各写一份"
这条结构性风险还在(`eec4b92` 已经漏过一次),所以靠 `--pagereserve` 兜住。

**落地实例**:`SettingsView` / `RecommendView` / `ArtistView` / `ArtistSongsPageView` /
`ArtistAlbumsPageView` / `PlaylistView` / `AlbumView` / `SearchView` / `RecentPlaybackView` /
`AccountView`(它同时是容器查询 `accountHost` 的宿主,`page-scroll` 与容器查询不冲突)
/ `DebugView` 的根 `ScrollViewer` 都接了 `page-scroll`。

⚠ **两个要注意**:

1. 居中卡片、没有滚动容器的页(如 `PersonalFmView`)接不上 `page-scroll`;只有窗口够矮时才可能
   被遮,需要的话给它包一层带 `page-scroll` 的 `ScrollViewer`。
2. 嵌套在页内的二级滚动区(如歌单页里的横向卡片区)用别的 class(如 `tracks-scroller`),
   **不要**把 `page-scroll` 套到非根滚动容器上——它的滚动条留白按整页预留,往里套会撑出多余空白。

## 别用 RenderTransform 把"贴边"的子控件往外推:ScrollViewer 自带 ClipToBounds

**症状**:个性推荐「每日歌曲推荐」区块底部那条横向滚动条,底边被切掉一截
(用户原话:"设置渲染距离向下了一点,但底下一半被裁剪了");第一版修法去掉位移后虽然不再被切,
用户又反馈"像是把进度条往上拉了,我期望的是往下一点但不会被裁剪" —— 两个方向都是这一条机制。

这个坑修了两次,第二次的结论比第一次更重要 —— **"往下推"必须让容器长高,而不是改外层留白**。

**机制**:`ScrollViewer` 自身 `ClipToBounds = true`(实测:遍历滚动条的祖先,`ScrollViewer` 就在
`ClipToBounds` 名单里),而**横向滚动条占的是 ScrollViewer 模板 `Grid RowDefinitions="*,Auto"`
的最后一行** —— 它的布局下沿永远等于 ScrollViewer 盒子的下沿。于是任何把它"再往下推"的绘制
位移(`RenderTransform`)都直接落到裁剪矩形之外,**推多少切多少**:

| `SongGridView` 的写法 | 绘制下沿 − 容器高 | Avalonia 给该条算出的 Clip 高度 |
|---|---|---|
| `Border padding.Bottom=12` + `TranslateTransform Y=4` | **越界 4px** | 8px(条高 12 → 底部被切 4) |
| `Border padding.Bottom=8`,无位移 | 0px | 12px(完整),但滚动条整体高了 4px |
| **定稿**:外 `8` + `ScrollViewer Padding="0,0,0,4"` | 0px | 12px,且滑块落回原来那 4px |

**两层 padding 各管一件事,别混**(2026-09-20 第二次踩坑的根因):

- **外层 `Border.Padding.Bottom`**(8)= 滚动条下沿到**下边框**的视觉留白。它只把边框往下放:
  实测 ScrollViewer 盒子的**高度就是内容高**(内容 384 → 盒子 384,自动横条那 12px 不进高度),
  盒子的**上沿**又被 `Padding.Top` 钉住 ⇒ 改外层底部 padding **不会移动滚动条**,只是让容器变矮/变高
  (第二次踩坑:把 12 改成 8 就等于把边框上提 4px,用户读成"把进度条往上拉了")。
- **`ScrollViewer.Padding.Bottom`**(4)= 真正把滚动条往下推的那 4px。模板里
  `ScrollContentPresenter.Margin` 绑的就是 `Padding`(见 `Styles/Controls/Scrolling.axaml`),
  所以留 4px 内边距 ⇒ **盒子长高 4px、滚动条(最底一行)跟着下移 4px,内容区仍留在原处**。

**量"用户看到的那条线"要量 Thumb,不是量 ScrollBar**:主题给横向滑块叠了
`scaleY(0.35) translateY(-2px)`(`HorizontalThumbScale`),8px 的布局高只画出 **2.8px** 细线。
`ScrollBar` 完整落在裁剪矩形里 ≠ 滑块画到位,所以判据用滑块自身的绘制范围。
`TranslatePoint` 会把自身 `RenderTransform` 算进去、`GetTransformedBounds().Bounds` 不算,
**两者混用很容易把 4px 记成 8px**(第一版探针就栽在这上面):

```
cut    = 滚动条的布局下沿 + RenderTransform.Y − 最内层容器高        // > 0 即底部被切 cut 像素
inkGap = ScrollViewer 盒底 − 滑块绘制下沿(TranslatePoint 两角点算)  // 安全边,要求 ∈ [1,5]
```

**自然高度**:定稿回到 `6×64+20=404`(= 内容 384 + 上下 6px + 底部 8px 留白 + 1px×2 边框 + ScrollViewer 自己那 4px),
`--sg` 的断言与 `d:DesignHeight` 已同步。

**回归线**:`--sgbottom`(`src/ALyricEase.Headless/SongGridBottomProbe.cs`)。断言同时校验
`ScrollViewer.ClipToBounds` 为真、滚动条真的实化(条高/滑块高 > 0)、滑块的安全边既不为负
(**被切**)也不超过 5px(**没画到底 → 就是"像是把进度条往上拉了"那种回归**),避免空过;
同一轮还会把旧的 `+4` 位移**按代码加回去**做 A/B(现状 0px / 旧写法 4px),确认这条线真能测到回归。
截图落在临时目录:`songgrid-bar-fixed[-3x].png`、`songgrid-bar-old-transform[-3x].png`。

**可推广的结论**:

1. *想让"贴在容器边上的子控件"(滚动条、贴边装饰、气泡尖角)往容器外越一点,不能用
   `RenderTransform` —— 祖先的 `ClipToBounds`(ScrollViewer / Border 常设)一定会把它切掉。*
2. *要移动这类"钉在容器某条边上的行",改宿主的 padding 也没用(只挪动那条边),得让**容器本身
   变高/变宽** —— 优先找容器自己有没有"内边距会推内容"的模板绑定(如本项目 ScrollViewer 的
   `Padding → ScrollContentPresenter.Margin`),那是唯一"长高但不挪内容"的干净办法。*

## UI 线程静息时长睡 ~79ms,而 `WM_TIMER` 唤不醒它 —— 帧供给不会自走 60Hz

(2026-09-21。起因:用户反馈**"进度条动的卡卡的,但如果鼠标一直移动就会变顺"** ——
这句话本身就是诊断信息,它指向"送帧被某种和输入相关的东西卡住",不是"画得不够快"。
回归线 `--progress-cadence-real`,`src/ALyricEase.Headless/ProgressCadenceProbe.cs`。)

### 先把"卡"量成数字

同窗口、同播放状态,只改"Win32 队列里有什么":

| 相位 | RAF 交付 | **真正写入视觉** | 帧间隔 p90 | ≥50ms 停顿 | `Send` 定时器每拍超时 |
|---|---|---|---|---|---|
| 静息 | 26~29/秒 | **8.7~13.1/秒** | 78~81ms | **101~102 次/8秒** | 56ms |
| 灌 `WM_MOUSEMOVE` | 83~100/秒 | 28~34/秒 | 6.6ms | 4~56 次/8秒 | 3~4ms |
| 外部后台每 16ms 投一个空作业 | 69~81/秒 | 32~42/秒 | 30ms | **0 次** | 13ms |

⚠ 第二列**不是"交付"而是"写入"**,这是读这张表的关键:动画器的 60FPS 闸门让成簇的多帧里
只有第一帧真的写,所以**写入次数 ≈ 用户眼里每秒变了几次** —— 静息 **8.7 次/秒**就是
"一顿一顿"的直接来源。(只看"交付 26/秒"会以为还行,其实视觉只动了 8.7 次。)

### 排除链(每条都是同口径对照,别再重复做)

| 猜想 | 做法 | 结果 |
|---|---|---|
| 渲染被降级到 `Input` 优先级 | `ALY_DISPATCH_STARVE=3600` 关掉那条降级路径 | 静息仍 102 次停顿 ⇒ 否 |
| 内核定时器分辨率太粗 | `timeBeginPeriod(1)` + 回读 `NtQueryTimerResolution` | **进程本来就是 1ms**;抬到 0.5ms 后 102 vs 101 ⇒ 否 |
| 电源节流(EcoQoS) | `SetProcessInformation(ProcessPowerThrottling)` 关 `EXECUTION_SPEED` | 100 次停顿 ⇒ 否 |
| 页面太重(详情页 vs 首页) | 两页对照 | 只影响"灌输入能不能翻盘" ⇒ 否 |
| **我们自己的写入在拖累** | 挂一个**什么都不做**的裸 RAF 循环 | 间隔分布与动画器**逐位相同** ⇒ 否 |
| UI 线程在忙 / 在睡 | 后台线程 `Post` 空作业,量"入队→执行" | **0.1ms 执行**(p99 0.2/p99.9 0.3) ⇒ UI 线程无辜 |

⚠ 第二条差点被读成"这个变量无效":`timeBeginPeriod` 在**被标记
`PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION` 的进程里会被静默忽略**。
所以凡"抬分辨率没差别"的结论,必须先把**回读值**打出来再下 —— 静默失败伪装成"该变量无关",
会把整条排查带错方向。

### 结论:唯一自洽的解释

- 三个不同优先级(`Send`/`Render`/`Background`)的 100ms `DispatcherTimer` 静息时**统统晚 56ms**。
  `Send` 是最高的那档,**没有任何东西能抢占它** ⇒ 卡点根本不在优先级仲裁。
- 渲染帧请求也要等 **~79ms**,和 `DispatcherTimer` 在等**同一次唤醒** ⇒ 两者共用一个门。
- **外部一投递,帧立刻出来,`DispatcherTimer` 也立刻变准**(56ms → 3~13ms)。

⇒ **UI 线程静息时在长睡(≈79ms 一轮),`WM_TIMER` 打不破这次等待,只有"投递的消息"能。**
鼠标移动的本质就是不断投递输入消息,所以"一动就顺"。这也顺带解释了为什么
**组合动画不会被饿死**(它由合成器推帧),而 `DispatcherTimer` / RAF 会。

⚠ 两条由此推出的实操规则:

1. **`DispatcherTimer` 与 RAF 都不是"按你给的名义间隔在跑"** —— 静息时都被压到 ~13 次/秒
   (主页面的进度条动画因此只有 8.7 次/秒)。要报"这条驱动多快",必须**数它真的推进了几次**
   (`ProgressRenderAnimator.FrameRendered` / 探针里的 `AnimatorStats`),不许报名义间隔。
2. **UI 线程驱动的相位别取 `Environment.TickCount64`**(15.6ms 分辨率 ⇒ 位置被量化成阶梯),用 `Stopwatch`。

### 修法:`Infrastructure/UiFramePacer`

按需持有(引用计数),持有期间由一个后台线程按 ~60Hz 投一个**空作业**,把 UI 线程从长睡里
戳醒,让它把已排队的渲染请求 / `WM_TIMER` 作业一次性做掉。

| | 写入/秒 | 帧间隔 p90 | ≥50ms 停顿 | 最坏间隔 |
|---|---|---|---|---|
| 无泵 | 8.7~13.1 | 78~81ms | 101~102 次/8秒 | 93ms |
| **有泵** | **34.0~34.7** | **29~30ms** | **0 次/8秒** | 45~49ms |

旁证:有泵之后"灌鼠标"那一档不再带来多少提升(44.2 vs 34.5)⇒ **静息路径已经接近
"鼠标一直在动"的水平**;`Send` 定时器超时 56ms → 8ms,反过来印证了机制本身。

⚠ **三个必须记住的实现点(都踩过,而且第一个差点让我把这条修法判负)**:

- **持有状态必须是引用计数,不能是单个布尔**。应用里同时有两个进度条动画器(底部条 +
  详情页覆盖层),各自独立持有/归还;用布尔的话任意一个归还就把另一个也关掉。
  症状是**泵跑两三秒后停死**(累计投递冻结在 150),而读数看起来像"这个机制完全无效"。
- **线程只起一次,只为"投递开关"服务**,别按持有数起停:循环重启走的是"先归还再持有",
  反复 Stop/Start 既漏线程又可能把自己停死。
- **探针必须能读回泵的内部状态**(`UiFramePacer.Describe()` 给探针)。上面那个 bug 就是靠
  "相位起点自检"那行 `持有者=1 wanted=True 累计投递=…` 抓到的 ——
  **靠"读数没变"去猜"它有没有在跑",是这次绕弯的根源。**

⚠ 成本与边界:泵开着时 UI 线程不再长睡,空闲占用会略升,**所以必须在循环停下时立即归还**
(`StopFrameLoop` 与 `OnAnimationFrame` 里 `ShouldAnimate()` 转假两条路径都要还,
用 `_pacerHeld` 去重)。停的时候要真的停 —— 别让它变成"常驻 60Hz 空转"。
诊断旋钮 `ALY_NO_PACER=1` 用来做"有泵 / 无泵"同口径对照,改动这类机制时请保留。

## 歌词列表"再进去只显示一行":把机制列全,再逐条证伪

用户报:进详情页看过歌词后退出,**再进去时只看到当前那一句,其余行随后才补上**(偶尔又全部一起出来)。
这个项目的教训是"**稳的假象比噪声危险**",所以这次不猜 —— 把可疑机制列全,一条条做**能判死它的实验**。
全部在 `ALY_NP_MODE=roundtrip`(往返诊断快速档,一轮约 27 秒)里跑,1200×720 与 1920×1080 各一遍
(`artifacts/np_rt4_*` ~ `np_rt8_load.log`)。

| 可疑机制 | 判它的实验 | 结果 |
|---|---|---|
| 歌词被重新加载(`Reset()` 只剩占位) | 打印 `行数/HasLyric` | 全程 48 行 / True ⇒ 否 |
| 重进时列表丢滚动位置 | ①~⑦ 九点采样 | 偏移恒定、偏差恒 0 ⇒ 否 |
| 面板 `IsVisible` 切换导致容器重建 | 关面板再开,数实化行数 | 实化恒 5(1200)/12(1920) ⇒ 否 |
| 容器逐帧实化(采样太粗才看不见) | 40ms 一档加密采样 | 序列全程恒定、无爬升 ⇒ 否 |
| 页面在窗外时居中补间"冻住" | 窗外连切 6 句逐句采样 | 偏移照常收敛 817→…→1476 ⇒ 否 |
| 逐行 `BlurEffect` 的离屏层晚一帧才画出来 | 滑回后 480ms/780ms/1.4s 帧 vs 稳定帧 | `DiffRatio` 0.00% / 平均通道差 0.40 级(噪声底 0.08)⇒ 否 |

⚠ **两个夹具自身的坑(比结论更值得记)**:

1. **轻载夹具永远复现不了"负载相关"的现象。** 往返诊断默认把色团漂移和两个动画器全停掉
   (为了让像素对照有干净噪声底),于是每一帧都闲着 —— **"实化慢 / 布局被挤"这类只在满负载下
   发生的东西在这里根本不会发生**,于是很容易得出"这条机制不成立"的错误结论。已加
   `ALY_NP_LOAD=1` 保留生产负载(只停位置推进),**它是单变量差异,不是新夹具**。
2. ⚠ **`BackdropVisible: false` 会让"负载档"变成假的。** 第一版 `ALY_NP_LOAD=1` 的场景里色团层
   是隐藏的 ⇒ 漂移对着空控件动,**标签写着"有"、实际一帧都没重绘**。旁证:B-2 抓到 `0.00%`
   像素差 —— 满负载下不可能零差。判据必须用 `refs.Backdrop.IsDriftRunning` **读回真实状态**,
   不能信自己刚设的那个属性值(与上面"三条铁律"同源)。

**顺带确认的两条结构事实(以后改这条链路先看这两条)**:

- `NowPlayingView` 是**常驻覆盖层**(`NowPlayingOverlayController` 只改 `RenderTransform`),
  收起时 `NowPlayingPanel` 也**不会**被重置 ⇒ 点收起按钮这条路上 `LyricsPanel.IsVisible`
  全程为 true、容器一个都不回收。**"再进去"和"一直在页内"在视觉树上没有区别。**
- `LyricView` 里**唯一**会把当前句滚回视口的入口是 `SelectionChanged`
  (`ScrollIntoView` + 420ms 缓出居中,走 `RequestAnimationFrame`)。选中行不变就没人补做定位 ——
  本次复现里它没咬人,但这是真实的结构缺口。

**尚未被证伪的唯一解释**(交给用户用设置档位做 30 秒判定):当前句是**唯一不挂 `Effect`** 的那一行
(样式 `:selected → Effect="{x:Null}"`),其余行都要走"离屏层 + 模糊"。若那台机器上模糊层的出帧
晚于"无效果行",屏幕上就恰好是"当前句先出来、其余陆续补上"。
⇒ 让用户把「性能与体验」切到**最佳性能**(整档不挂 `Effect`)再走一遍同样流程:
症状消失即成立;不消失就该加**应用内埋点**(记 `行数/当前句/实化/偏移/视口` + 抓两张图),
而不是继续调夹具 —— 夹具已经把能问的都问完了。

### 现场证据与修法(2026-09-22 傍晚,在用户机上抓到)

模糊档也判负之后上了应用内埋点(`NowPlayingView` 里,`ALY_LYRIC_DIAG`,输出 `%TEMP%\aly-lyric-diag.log`),
用户复现一次即抓到**进页面那一刻容器被丢掉**:

```text
+0ms    实化=8   容器[5:1.00 1:0.70 2:0.70 3:0.70 4:0.70 6:0.80 7:0.60 8:0.40]  内容=3700 偏移=163
+80ms   实化=2   容器[5:1.00 0:0.44]                                          内容=3762 偏移=163
+240ms  实化=2   容器[5:1.00 0:0.55]                                          内容=3762 偏移=163
+480ms  实化=8   容器[5:1.00 1 2 3 4 6 7 8]                                   内容=3384 偏移=163
```

偏移一动不动,**`内容` 却在 3268↔4049 之间乱跳** —— 这就是 `VirtualizingStackPanel` 在反复
Measure/Arrange:容器被回收又重建,而"当前句"因为是锚点所以活到最后 ⇒ 屏幕上**只有当前句 + 一片空白**,
约 0.4 秒后其余行补回来。用户描述的"只有一行、其余陆续出现、时而正常"全部对上。
(他的窗口 1270×1011,歌词面板只有 579 宽 ⇒ 行高 95~145 不等,正是估算最难准的那种。)

**修法:`ItemsPanel` 上显式给 `CacheLength`。** 它默认 **0.0**(范围 0~2),官方文档原话是
*"0.5 = 上下各缓冲半个视口;会实化更多元素,但**大幅减少 Measure-Arrange 周期**"* ——
正好打在抖动成因上。`LyricView.axaml` 的 `ListBox#LyricList` 里改为
`<VirtualizingStackPanel CacheLength="1" />`;夹具复测 `内容` 序列从乱跳变成
`6016 → 6016 → 6000 → 6000` 纹丝不动,实化 8 → 22~23(仍是虚拟化)。

**同轮实测代价**(1200×720,`ALY_NP_MODE=lyric-ab`,对照 `artifacts/np_gpu_lyric_gate1.log`):

| 档位 | 改前 | CacheLength=1 | 实化行数 |
|---|---|---|---|
| 最佳质量(挂模糊) | 6.38% | **7.15%** | 5 → 15(带模糊 14) |
| 最佳性能(不挂模糊) | 5.43% | **5.60%** | 5 → 15 |
| 关歌词面板(标尺锚) | 4.76% | 4.88% | — |

⇒ 多出的 **0.77% 全花在"多实化的 10 行带模糊"上**(性能档只 +0.17%)。这顺带证实了一条
此前只是推测的事:**缓冲区里那些滚出视口的模糊行并没有被完全裁掉,仍在按行花钱** ——
所以"实化得多"不是免费的内存换稳定,而是要按行付 GPU。嫌贵就把 `CacheLength` 降到 0.5
(半个视口)或引导用户走「最佳性能」;回到 0 抖动就回来。

### 上游根因(2026-09-22 定案):`VirtualizingStackPanel` 的"游离容器"

同一机制的两副面孔:① **只剩当前句 + 一片空白**(`区间=0..0`);② **同一句被画两遍**(用户报的"重复、重叠")。
现场证据(用户机 1270×1011,存档 `artifacts/lyric_diag_user_run3.log`):

```
容器[-1:67h/1.00/-   4:67h/1.00/-   3:67h/0.70/b1.5   2:67h/0.70/b1.5 ...]
     ↑ IndexFromContainer = -1,还带着 :selected 的高亮(1.00) —— 不在面板的实化模型里
```

⇒ 铁律:**容器数 = 模型跨度 + 1**(35 = 34 + 1)。那个 `-1` 是**游离容器(orphan)**,身上留着
"被定位那句"的样式与内容,又因为不在模型里、排布位置是上一次留下的 ⇒ 正好压在真句上。
`CacheLength` 救不了它:缓冲是按**面板自己认的模型**算的,模型错了缓冲跟着错。

**为什么偏偏是"美人鱼"这首**:该曲歌词数据里标题行 `[00:00.00]美人鱼` 正好是**索引 4**
(前 4 行是 作词/作曲/编曲/制作人),而日志那一刻 `当前句=4 选中=4` ⇒ 游离容器复制的就是它。
⚠ 先证伪了"数据重复":全缓存 203 个 `l-*.lyrics` **逐字节**检索,"美人鱼"只出现 **1 次**。
⇒ 凡是"某句重复",**先查数据再查 UI**;把数据排掉之后,`IndexFromContainer=-1` 就是唯一的解释。

**上游**:[#15194](https://github.com/AvaloniaUI/Avalonia/issues/15194) —— "列表**隐藏期间**滚动/改内容
⇒ 显示时出现重复/幽灵容器",**至今 open,且已确认在 Avalonia 12 上仍复现**
(最小复现仓 `BobLd/VirtualizingStackPanelDuplicatesIssue` 2026-03 更新到 12;维护者原话
"复现容易,修不容易")。相关:[#15449](https://github.com/AvaloniaUI/Avalonia/pull/15449) 修的是同一族里的另一半 ——
`ScrollIntoView` 在**项高可变**时会把"要滚到的那项"留成 orphan(已并入 11.2,但 12 上仍能撞到别的入口)。
另有一条源码事实要记住:`ScrollIntoView` 里有 `if (!IsEffectivelyVisible) return null` ——
**不可见时滚动请求被静默丢弃**,于是"隐藏期间"发生的选中/滚动不会落到模型上,窗口一显形就错位。

**本项目怎么撞上的**:`LyricList` 的 `IsVisible` 绑 `HasLyric`,而**每次切歌** `LoadAsync` 都先 `Reset()`
⇒ `HasLyric=false` ⇒ 列表隐藏;紧接着新歌词灌入、`SelectedIndex` 又跟着 `CurrentIndex` 变 ——
这些**全落在"不可见"或刚恢复的那一瞬间**。用户日志的形状就是这么来的:

```
行数=0  HasLyric=False  列表可见=False
行数=59 HasLyric=True   列表可见=True  实化=1  区间=0..0  选中=-1
[heal] 切句 #1 容器=2 模型跨度=1
容器[-1:1.00,  4:1.00, 3, 2, 1, 0]
```

**应用侧三条出路**(上游不修,只能绕)——**A+B 已落地(2026-09-22)**,C 留作后备:

- **✅ A(1 行)**:空/加载态**不再隐藏 `ListBox`** —— 原 `IsVisible="{Binding HasLyric}"` 换成
  `IsHitTestVisible="{Binding HasLyric}"`(输入行为与原来等价),直接消掉"隐藏期间变更"这个触发条件。
  空态不会变脏:没有歌词时列表本就是空的(自身 `Background=Transparent`、无边框 ⇒ 一个像素都不画),
  空态文字由那个独立的 `Message` 覆盖层负责。
- **✅ B**:滚动/定位动作按**有效可见**门控 —— `LyricView` 订阅**自身 + 全部祖先**的 `IsVisibleProperty`
  (⚠ `IsEffectivelyVisible` 在 Avalonia 12 **只有 getter**、不可订阅,和 `IndeterminateAnimationGate` 同一个坑),
  不可见时**不发 `ScrollIntoView`**、掐掉在途补间,改记账,重新可见时补一次;`HealRealization` 同样
  不在不可见时出手(否则白烧那 3 次有界配额)。
  ⚠ 页面是**平移出窗外**(`RenderTransform`)隐藏的,`IsVisible` 全程不变 ⇒ 订阅链不会触发,
  所以"进页面"时必须在 `OnPageShown` 里**显式补做**,且只有真攒过才补(健康路径上多叫一次
  `ScrollIntoView` 本身就是在给这个缺陷递机会)。
- **C(后备,确定性但有代价)**:这块列表放弃虚拟化(30~60 行)⇒ 整族问题消失,但按行收费:
  上表里"多实化 10 行带模糊"就是 +0.77% GPU,全实化要按总行数付。

**A+B 的夹具回归**(`ALY_NP_MODE=roundtrip`,1270×1011 / 1920×1080 两轮,`artifacts/np_rt21_ab.log`、`np_rt22_ab.log`):
**零 `[heal]` 误触发、零 `区间=0..0`**,播放各路径实化恒 22~23、偏差恒 0。同时改对了一处**夹具语义**:
路径 D 原本是"清空 + 靠 `HasLyric` 隐藏列表"来复刻用户现场,`A` 之后那句绑定没了、标签就成了假的 ⇒
已改成**真实换歌三步**:`Lines` 清空 + `CurrentIndex=-1` → 灌入新集合 → **首次定位**。
改对之后这条路径给出一条新知识:

| 采样点 | 实化@偏移 |
|---|---|
| D·重载中(空集,列表仍可见) | 0 —— `内容` 塌成 897、偏移被夹到 0 |
| D·重载后(尚未定位) | **8@0** —— 只实化一个视口、缓冲没上 |
| **D·首次定位后** | **22@1889** —— 立刻回到健康态(居中、缓冲生效) |

⇒ 那个"只实化一个视口"的态是**瞬态**,真实播放里 200ms 内就被进度回调的首次定位修掉;
但它也提醒:**`A` 换来一个新副作用 —— 空集可见时 `ScrollViewer` 会把 `Extent` 夹成视口、
`Offset` 归零**(以前列表被隐藏、不参与测量,偏移能留住)。这条在用户那条路径上无害(定位随后就跑),
但若哪天看到"换歌后从顶部慢慢滚下去"变明显,就是它的账。

### 用户续报"幽灵没了,但进屋还是过一会才加载全" ⇒ 真根因:**页面平移出窗外时还在做定位**(2026-09-22)

先记住一条:**B 那处门控只订阅了 `IsVisible`,而详情页收起是"整页 `RenderTransform` 平移出窗外"
—— 全程没有任何 `IsVisible` 变化 ⇒ 它照样认为"可见",于是页面在窗外时每条切句都照跑
`ScrollIntoView` + 居中补间。** 而离屏时容器位置的 `TranslatePoint` 不可信,补间会把偏移拖到离谱的地方。
用户机上抓到的原话:

```
21:45:56.202 当前句=11  偏移=792    区间=0..28    ← 正常跟着唱
21:45:57.817 当前句=12  偏移=4382   区间=29..54   ← 2 秒内被拽到 4382(当前句才第 12 行!)
21:45:59.762 [打开详情页] +0ms  实化=2  区间=0..0   ← 一进屋先塌
             +160ms 区间=5..9 → +240ms 区间=0..14 → +480ms 区间=0..27  ← 从 0 号逐页实化回来
```

⇒ "过一会才加载全"= **进屋后先纠正那个被拽跑的偏移**:模型塌成 `0..0`、再从 0 号逐页实化,
约 0.5 秒补齐。**修法 `LyricView.OnPageHidden`**(由 `NowPlayingView` 在 `ShowNowPlaying=false` 时调用):
收起期间把 `_pageParked` 置真 ⇒ `IsEffectivelyPresentable()` 变假 ⇒ 一律只记账(`_pendingRecenterIndex`)、
并掐掉在途补间;`OnPageShown` 里解封并把记账的那一次补上。⇒ 离屏期间不做任何不可信的定位计算。

⚠ **回归线的期望值随之变了**(`ALY_NP_MODE=roundtrip` 的 B1 段,`artifacts/np_rt24_park.log`):
正确读数是"**每格内那 4 个 `Offset.Y` 完全相同**"(= 我们那条 420ms 补间真停了),而**跨格仍会挪约一行高**
—— 那是 `ListBox` **自己**为"让选中项保持可见"做的最小滚动,不是我们的补间。别把它读成"定位还在跑"。
判据仍在 ⑦:进屋后应当**一次**补正到位(偏差 0),而不是先塌成 `0..0` 再逐页实化。

### 上面全试完还犯 ⇒ 换方向:**升 Avalonia 12.1.1 → 12.1.2**(2026-09-22)

用户机上反复抓到的**另一副面孔**(跟偏移无关):当前句就在最上面、`偏移=0`,面板却只实化 4~6 行就停,
而列表高 **897**——即"面板以为自己只有一小块视口要填",同时 `内容`(Extent) 长期在 `897↔6011` 之间乱跳。
这与 `VirtualizingStackPanel` 的**自适应尺寸/变高项**路径高度吻合,而 **12.1.2(2026-09-02)里正好有三条**打在这一点上:

| 上游修复 | PR |
|---|---|
| `Controls – Fix auto-sized VirtualizingStackPanel not rendering` | #22081 |
| `Controls – Fix VirtualizingStackPanel ScrollToEnd with variable-sized items` | #22014 |
| `Controls – Fix multiple ScrollContentPresenter.BringDescendantIntoView calls` | #22001 |

⇒ 已把 4 个 csproj 里**所有** `Avalonia.*` 引用从 `12.1.1` 升到 `12.1.2`
(`Avalonia`、`Desktop`、`Themes.Fluent`、`Fonts.Inter`、`Headless`、`Skia`、**`Android`**)。
⚠ **漏一个就直接 `NU1605` 依赖降级报错**,而且会**分两次咬人**:第一次漏的是 `Avalonia.Skia`(Headless 工程),
补上后 Android 又报 `Avalonia.Android` —— 它把 `Themes.Fluent`/`Fonts.Inter` 以旧版本传递进来,撞上我们的直接引用。
教训:**别按"我记得有哪几个"改,直接 `grep 'Include="Avalonia'`(含 `Avalonia.Android` 这类后缀)一次列全。**

**夹具在 12.1.2 下的回归**(`artifacts/np_rt27_1212.log`,1270×1011):与 12.1.1 **逐项一致**
(⑥收起后 22 行/偏差 0、B1 每格内恒定、⑦ 收敛到 2626、D 重载后 8@0 → 首次定位 22@1888、E 段稳定)。
⇒ **夹具本来复现不出这条症状**(它的进入路径一直健康),所以"修没修好"只能由用户机的埋点判定;
这次升级属于"靶向上游、低风险、可回退"的一步,不是已验证的结论。

⚠ 注意 `#15194`(列表隐藏期间改内容 ⇒ 幽灵容器)**至今仍 open**,与上面的修复无关 —— 那条仍靠
"别用 `IsVisible` 隐藏 + 定位按有效可见门控"这套应用侧绕法。

### 2026-09-23:这批绕法**已全部删除**(只留两处真正必要的)

12.1.2 落地后按"能删就删"清理,`CacheLength=1`、实化自愈(`HealRealization`/`HealReporter`/三招重试)
与相关的诊断钩子全部移除,列表回到**默认** `ItemsPanel`。删除前存了回退补丁:
`artifacts/lyric-workarounds-before-removal.patch`(⚠ 这些代码当时都在未提交的工作区里,不在 git 历史中)。

**保留的两处(都与"虚拟化面板"无关,不能跟着删)**:
1. `IsHitTestVisible="{Binding HasLyric}"`(取代 `IsVisible`)—— 挡的是 `#15194`,上游仍 open;
2. `_pageParked` / `OnPageHidden` + 定位按"有效可见"门控 —— 挡的是**我们自己**那条 420ms 居中补间
   (离屏时 `TranslatePoint` 不可信,实测把 `Offset` 拽到 792→4382)。

**夹具复测**(`artifacts/np_rt30_clean.log` 1270×1011 / `np_rt31_clean.log` 1920×1080):
⑥收起后 `实化=8 内容=6050 偏差=0`、B1 每格内偏移恒定、⑦ 收敛到 2633(偏差 0)、
D 重载后 `8@0` 稳定 → 首次定位 `8~9@1888`、E 段稳定 ⇒ **`内容` 全程精确、无塌陷、无抖动**。
实化数从 22~23 降到 **8~9**(= 默认配置下"只实化一个视口",正是本该的样子)。

**GPU**(`artifacts/np_gpu_clean_1200.log`,1200×720,同轮拉丁方,帧节奏/状态体检全绿):
质量档 **7.12%**(带模糊 4/5 行)/ 性能档 **5.65%**(−1.47%)/ 关歌词面板 **4.81%**(−2.31%)。
⚠ **跨轮不可比**:本轮质量档比 09-22 那次同尺寸的 6.38% 高 0.74%(两次实化行数同为 5),
但跨次读数受机器上其它负载影响(本项目自己的教训:同构建跨次能差十几点)⇒ **"12.1.2 是否带来
0.7% 的 GPU 代价"与"删掉 `CacheLength` 省了多少"都没结论**,要判就得另做同轮对照。本轮只有
**轮内差**可信(模糊 −1.47%、面板 −2.31%)。

## `AutoScrollToSelectedItem` 默认 true:框架会在选中变化时自己滚,应用侧抑制拦不住

(2026-09-30,`--lyrscroll` 探针实证)

**症状**:歌词面板"手动滚动后 4 秒暂停自动跟随"从未生效 —— 打标、抑制分支全走对了,
下一句变化列表还是被拽回。拉回源头根本不在应用代码:`ListBox.AutoScrollToSelectedItem`
**默认 true**(挂在 `SelectingItemsControl` 上),`SelectedIndex` 一变 ListBox 自己
`ScrollIntoView`,绕过一切自定义滚动逻辑。

**修法**:`LyricView.axaml` 的 LyricList 显式 `AutoScrollToSelectedItem="False"`,
居中滚动只允许 `OnSelectionChanged → Recenter` 一条路径驱动(它自己会先 ScrollIntoView 物化
容器再做 420ms 补间,不依赖框架那一下)。

**教训**:在自绘滚动列表上,先数清**所有**会写 `ScrollViewer.Offset` 的路径 —— 应用补间、
框架自动跟随、快照恢复(`DetailPageScrollController`)、`ScrollIntoView` —— 再谈抑制。
"自己代码里没人拉"不等于"没人拉"。

## 样式里的 Effect 是共享实例:运行时改它的属性 = 全部匹配行一起渐变

(2026-10-01,`--lyrblur` 探针实证)

`Style` Setter 里的 `<BlurEffect Radius="5"/>` 只创建**一个**实例,所有匹配该样式的控件引用的是
同一个对象。由此得到一个便宜的"批量渐变"手法:把 Effect 抽成 `UserControl.Resources` 里的共享
资源、样式用 `StaticResource` 引用,运行时只改共享实例的属性,全体行一起变。

三个前提都成立(已验证):
1. **`BlurEffect : Animatable`** ⇒ 可以在 XAML 里给它挂 `DoubleTransition`(Property="Radius"),
   改一次目标值自动 0.35s 渐变;
2. **改半径会重绘**:`Visual.EffectProperty` 注册在 `AffectsRender` 里,Effect 实现了
   `IAffectsRender.Invalidated` 弱事件 ⇒ 共享实例 Radius 一变,每个引用它的 Visual 都 `InvalidateVisual`;
3. **Composition 动画做不了这件事**:Composition 只能驱动合成器属性(Translation/Opacity/Scale 等),
   Effect 在 Skia 元素渲染侧,没有暴露成合成器可动画属性。

⚠ 两个坑:
- **半径 0 ≠ 免费**:半径 0 的 Effect 仍走「离屏层 + 图像过滤」管线。渐变到 0 之后必须再挂一个
  `Effect=x:Null` 的类(同优先级、声明在后)把 Effect 真正摘掉,长时间停留状态才不白烧 GPU;
- **XML 注释里不能出现双连字符**(如探针名 `--lyrblur`),AVLN1001 "An XML comment cannot contain '--'"。

## Effect 渐变:共享 BlurEffect 实例 + DoubleTransition,不是 Composition 动画

(2026-10-01,`--lyrblur` 探针实证)

**问题**:歌词"手动滚动时模糊要有动画地取消、4 秒后动画地恢复"。`Effect` 属性本身没有
Transition 类型(WPF 也没有),Composition 动画只能驱动合成器属性(Translation/Opacity/Scale),
`BlurEffect` 在 Skia 元素渲染侧、未暴露成合成器属性,Avalonia 也没有 EffectGraph ⇒ 只能在元素侧做。

**机制**(三个前提都能立住):
1. 样式 Setter 引用的对象是**共享实例** —— 把各模糊档位抽成 `UserControl.Resources` 里的
   `BlurEffect` 资源,样式 `<Setter Property="Effect" Value="{StaticResource ...}"/>`,
   全部匹配行引用同一个对象;改一个 `Radius` 全体生效。
2. `BlurEffect` 是 `Animatable`,可以直接在 XAML 里挂 `<BlurEffect.Transitions><DoubleTransition .../>`,
   属性值一变动画自动跑(改变目标值会从当前值续跑,连续打标不会跳变)。
3. `Visual.EffectProperty` 注册在 `AffectsRender` 里,且 Effect 自身实现 `IAffectsRender.Invalidated`
   弱事件 ⇒ 实例的 `Radius` 变化会自动让挂它的 Visual 重绘,不用手动 InvalidateVisual。

**别忘了第二阶段**:半径 0 ≠ 免费 —— Effect 挂着(哪怕半径 0)就走「离屏层 + 图像过滤」管线。
渐变到 0 之后要用类把 Effect 真正置 `x:Null`(本项目 `blur-suspended` / `no-blur` 两兄弟);
恢复时先摘类(共享实例以半径 0 重新挂上)再让 Transition 把半径渐回档位值,即得渐显。

**注意**:XML 注释里不能出现 `--`(探针名写成 `lyrblur`,别带横线,AVLN1001);
判"行为对不对"先证伪探针(打印了过期变量 → 假失败),诊断打点看 gen/定时器时序最快。
