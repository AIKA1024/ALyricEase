# 用户页与关注/粉丝名单

> 代码：`ViewModels/UserProfileViewModel.cs` + `Views/UserProfileView.axaml`（用户页）、
> `ViewModels/UserRelationsPageViewModel.cs` + `Views/UserRelationsPageView.axaml`（名单页）。
> 接口字段见 [netease-api/02-endpoints.md](netease-api/02-endpoints.md) §E5。

## 1. 结构

用户页（原版 UWP 结构）：英雄卡（头像采样背景 / 昵称 / 等级 / 关注·粉丝 / 签名）+
三个分组（音乐品味、参与创作、收藏）。双音源：网易云 `uid=0` 解析成自己、他人按 uid；
QQ 自己走账号摘要、他人走 `encrypt_uin`。

**头部两个计数的来源**（三处各不相同，改错一处就是"显示 0 / 点进去又有"）：

| 场景 | 来源 |
|---|---|
| 网易云（自己/他人） | `GET /api/v1/user/detail/{uid}` → `profile.follows` / `profile.followeds` |
| QQ 他人页 | `fcg_get_profile_homepage` → `creator.nums.follownum` / `fansnum`（`GetUserHomepageAsync`）|
| QQ 自己的页 | 同一条主页 fcg，由 `GetAccountSummaryAsync` 顺带取（`MusicAccountSummary.Follows/Followeds`）|

⚠ 自己的 QQ 页曾经恒为 0：`LoadQqAsync` 只从账号摘要拿昵称/头像/等级，而摘要里没有计数 ——
页面照常渲染但两个数字是 0，点进名单却有数据。修法是给摘要补 `Follows/Followeds`
（`GetSelfRelationCountsSafeAsync` 取 `creator.nums`，失败回 0 不拖垮账号卡）。

⚠ `LoadQqAsync` 必须清 `_euin`：QQ 名单的 `HostUin` 留空才表示"自己"。从他人页返回自己页时
残留上一个 euin 会让"关注"点开的是**别人**的名单。`LoadAsync`（网易云）同样清一次。

## 2. 关注 / 粉丝名单（2026-11 新增）

**入口**：英雄卡里"关注 N · 粉丝 M"是**一个** `Button.hero-link`（外观等同文字，
悬停浮出 `#22FFFFFF`，`Padding="6,2"` + `Margin="-6,0,0,0"` 保持原纯文字位置）。
点击进入名单页，默认落在"关注"Tab，页内再切换到"粉丝"。

**页面**：`UserRelationsPageViewModel` 一个 VM 同时承载两份名单：
顶部是 Fluent 2 TabList（`ListBox.relation-tabs`：文字 + 计数徽标、选中项半粗字重 +
2px 强调下划线、整条 1px 分隔线）。页键 `"UserRelations"`，`DetailPageKind` 仍用
`UserFollowing` / `UserFollowers` 两个值（快照要知道回来时停在哪个 Tab）。

**Tab 切换**：两份名单在 VM 内各自保留 `RelationState`（行数据、offset、QQ stage、
`HasMore`、空态文案）。切到未加载过的 Tab 才联网拉首页；切回已加载 Tab 直接复用，
不重复请求。切换时 `RestorePageScrollState(0)` 回到顶部。
⚠ `ResetContent` 必须先把 `SelectedTabIndex` 置 -1、填完 `Tabs` 再写目标值：
页面视图可能早于数据创建，目标值若与旧值相同就不会触发通知，ListBox 不会选中后到的条目
（表现为"内容是关注、关注 Tab 却没选中"）。
⚠ `ListBox` 必须设 `AutoScrollToSelectedItem="False"`，否则新视图恢复时 ListBox 会把
选中 Tab `BringIntoView`、冒泡到页面 `ScrollViewer`，把返回后的滚动位置从 240 拽回 ~95。

**分页**：`PageSize=30`，滚动近底部（剩余 < 500px）由视图调 `LoadMoreAsync`（单飞）；
服务端 `more` 驱动 `HasMore`，请求失败也把 `HasMore` 置 false（否则滚到底会无限重试）。
`DetailPageScrollController` 与其它详情页同款。⚠ QQ 的"关注"是两段（先用户后歌手），
快照里要存 `RelationStage` 才能从正确的一段续拉；分页 offset/stage 只在请求确认仍属于
当前 Tab 后提交，避免切 Tab 取消请求时把位置推进到没有行数据的地方。

**点行**：`Tapped` → `MainViewModel.OpenUserCommand.Execute(userId)`（与搜索结果同一入口），
所以对方页是普通 `UserProfile.LoadAsync(uid)`，不是 `uid=0` 的"自己"。

**返回**：名单整页进一次性内存快照（`DetailPageCacheData.RelationUsers`），
返回时不再联网、滚动位置与当前 Tab 一并还原；另一份名单未加载时保持未加载，
切过去再拉。`DetailNavigationSnapshot` 额外存两份名单的计数，用于恢复 Tab 徽标。
⚠ 名单为空时**不写** `RelationUsers`（`null` 而不是空数组）—— restore 侧看到 null
会重新拉首页，避免"网络失败后返回是一片空白且永不重试"。

**标题**：`Title` 在 `LoadAsync` 里定为"关注与粉丝"（两份名单共用一个页面），顶部横幅经
`MainViewModel.OnUserRelationsTitleChanged` 跟随；页内人数看 Tab 徽标，
副标题只显示"来自 {昵称}"。

## 3. 加载态

首屏 `IsLoading` 期间显示**居中**的转圈 + "加载中…"：转圈用全局 `Ellipse.spinner`
（`Styles/Controls/Common.axaml`，Avalonia 12 基础包没有 ProgressRing，用 Ellipse 弧线自绘，
`StrokeDashArray="8,14"` 前半段是可见弧长）；底边吃 `PlayerBarReserve`，让它在"内容区"里居中
而不是整窗居中（否则被播放条压着，视觉偏下）。加载层在 `ScrollViewer` 之外、`IsHitTestVisible=False`，
不随滚动漂移也不吃点击。

⚠ **每个使用点必须自己写 `StrokeDashArray`**：全局 `Ellipse.spinner` 只负责 `RenderTransform`
和旋转驱动，**不提供弧线**。漏了就是一个闭合整圆环，而整圆旋转对称 ⇒ 合成动画再顺也看不出在转，
用户只会看到"按钮里凭空多了个空心圈"（2026-11-11 歌手页关注按钮实测踩到；该处最终按设计去掉了进度环，见 §4.1）。
虚线值按 `StrokeThickness` 的倍数算：14px 圈 + 描边 2 的周长约 17 个单位，`5,39` 得到单段
约 108° 的短弧（探针逐角度量过），`12,32` 会变成 255° 的大月牙。全项目其它 4 处是
`8,14`（20px 圈 / 描边 3）。

此前只有内容根 `IsVisible="{Binding !IsLoading}"`：加载期间整页空白，数据到位才突然跳满 ——
用户页要连拉「资料 + 歌单」（QQ 还多两次），这个空窗最明显。名单页首屏也换成同一套
（18px 转圈 + `page-sub`），分页加载 `IsLoadingMore` 仍只显示行内小字。

**怎么才不卡**（2026-11 用户反馈"这个转圈卡卡的"）：旋转原本是 UI 线程 styling 动画，
静息时 UI 线程长睡 ~79ms（见 [perf-notes.md](perf-notes.md) §九）⇒ 只有 ~12fps，肉眼就是一顿一顿。

正解是**合成动画**（`infra:SpinnerRotation.IsActive`）：用 `CompositionVisual.RotationAngle`
在渲染线程转，和 `CompositionPageTransition`、模态弹层、侧栏抽屉同一条路子，**完全不占 UI 线程**
（实测 `UiFramePacer` 持有者为 0，UI 线程照常睡）。三个坑：

| 坑 | 说明 |
|---|---|
| 单位是**弧度** | `0 → 2π` 一圈（`MathF.PI * 2`），不是 360 |
| 原点在左上角 | 必须 `visual.Size = 控件尺寸` + `AnchorPoint = (0.5, 0.5)`，否则是绕角"公转"而不是自转 |
| 隐藏不等于停 | Avalonia 对隐藏但仍挂在树上的元素照旧推进动画时钟 ⇒ 附加属性在"自身+祖先可见、窗口非最小化"时才 `StartAnimation`，否则 `StopAnimation` 并把角度归零 |

取不到 `CompositionVisual` 时退回 `Ellipse.spinner.running` 的样式动画 + `UiFramePacer` 租约
（慢但能动）；两条路径**互斥**，同时开会叠加成双倍转速。

验证：`--spinner-motion-real`（真窗口 + 外部抓屏）—— 合成动画不写回 UI 属性，`RenderTargetBitmap`
拍不到旋转（实测两帧逐像素相同），只能数屏幕像素。结果：旋转期 5/5 帧有变化、`pacer 持有者=0`；
置 `IsActive=false` 后连抓两帧逐像素相同。

**加载完成时内容淡入**（180ms，`infra:FadeInWhenShown.IsEnabled`）：只切 `IsVisible` 的话，
转圈一消失整页就"啪"地跳出来（用户原话"有点突兀"）。同样走合成层 `Opacity` 0→1，
不用 XAML `Transitions`（UI 线程逐帧，静息时 150ms 会被拖成 ~1s）。

三个必须注意的点（都踩过）：

- **压透明必须同步**（2026-11 用户第二次反馈"还是一下子出来，有过渡但很奇怪"）：渲染读的是
  **UI 层的 `Visual.Opacity`**。原来只在 `Dispatcher.Post(Render)` 里压合成基值，这个作业一旦晚于
  那次渲染，屏幕就先画出**全亮的一帧**再跳回半透明淡入 —— 真窗口抓屏抓到
  `[0, 0, 246, 246, 169, 214, 236, …]`（先闪 2 帧满亮度）。修法：在 `IsVisible` 变 true 的
  属性回调里**同步** `control.Opacity = 0`，`Post` 只负责起合成动画。修后
  `[0, 0, 1, 115, 181, 219, 233, 243, 245, 246, …]` 单调上升。
- **不能一收到 `IsVisible=true` 就取 `CompositionVisual`** —— 刚变可见的控件还没重新进渲染队列，
  取到的是 null（实测）。所以起动画那一步仍要 `Dispatcher.Post(..., DispatcherPriority.Render)`；
  拿不到 visual 时要把 UI 层 Opacity 恢复成 1，别把内容留在透明态。
- **收尾顺序：先抬 UI 层 `Opacity`，再 `StopAnimation` 合成动画**。反了会有一帧回落到 0
  （内容闪一下变透明）。基值也要写回 1，否则之后任何 `StopAnimation("Opacity")`
  （页面过渡、模态弹层都会调）会把整块拉成透明。

真窗口实测（`--fade-timing-real`，结构照抄用户页：`Panel > ScrollViewer > 内容根`，
白块从黑背景淡入，每 ~10ms 抓一帧）：修前 `[0, 0, 246, 246, 169, 214, 236, …]` = 闪帧；
修后 `[0, 0, 1, 70, 131, 181, 218, 241, 246, …]` = 干净渐变。
⚠ 这条只能抓屏验：`--user-relations` 里的属性断言（UI 层=0 / 合成基值=0 / 结束=1）
在旧实现下**也能通过**，抓不到"压晚了"。抓屏受遮挡时探针报 `SKIP`（基线≠0 或峰值<200），
不要当缺陷——`Topmost` 也不是万无一失，用户在动别的窗口时会抓到别人的画面。

曲线也有讲究：原来用 `CubicEaseOut`，1/4 时长处就到 ~60%（`[0,0,1,152,…]`），
观感仍是"闪一下就到位"。换 `SineEaseOut` 后中段才过半，同样 180ms 却看得出连续变化。

## 4. QQ 怎么找到名单（2026-11 反编译 + 抓包核实）

**桌面客户端（22.52）里只有"关注"，没有"粉丝"名单。** 反编译 `D:\Software\QQMusic\QQMusic_Protocol.dll`
（REA/Ghidra，sha256 `d098182a33a9…`，x86）后，模块串在 `.rdata` 0x10129a20：

```
music.concern.ConcernSystem.cgi_get_all_concern_list   # 关注(用户与歌手混排,条目只有 uin / singer_id / time,无昵称头像)
Concern.ConcernSystemServer.cgi_concern_user           # 关注/取关动作(不是名单)
```

`GetFansList` / `RelationList` / `GetFollowUserList` / `GetFollowSingerList` 在客户端里**一个都没有** ——
PC 端的"关注"页就是上面那条混排接口，粉丝只能在 App / 网页看。老文里那个
`friend_follow_or_listen_list.fcg`（2019 年 QQMusicApi 用的）现在直接 404。

**粉丝名单在网页版里**：`y.qq.com/ryqq/js/common.chunk.*.js` 的 `p.h`：

```js
{ module: "music.concern.RelationList", method: "GetFansList"|"GetFollowUserList"|"GetFollowSingerList",
  param: { From: start, Size: num, HostUin: encrypt_uin } }   // HostUin 留空 = 自己
// 回包 data: { Total, HasMore, LockFlag, List: [{ EncUin, Name, Desc, AvatarUrl, FanNum, IsFollow, MID }] }
```

**实测结论（2026-11-05，本机登录态）**：

| 事实 | 值 |
|---|---|
| 三个 method 都要登录 | 匿名回 `req_0.code = 1000`「未登录」，不是空列表 |
| `HostUin` 是 **encrypt_uin**，不是数字 uin | 别人的名单直接传 `EncUin`（与搜索结果同一种 id） |
| `Total` 与主页 `nums` 对得上 | `followusernum=8` / `followsingernum=32` / `fansnum=7`，三个 method 分别回 8 / 32 / 7 |
| 关注总数 = 用户 + 歌手 | 主页 `follownum = followusernum + followsingernum`（40 = 8 + 32）|
| 条目 `FanNum` = 对方粉丝数 | 没有对方的关注数 |

**因此实现上**：QQ 的"关注"名单 = `GetFollowUserList` + `GetFollowSingerList` 两段拼成一份
（用户段最后一页和歌手段第一页合并返回 —— 用户少时页面不足一屏，滚动事件不发生，
只靠滚动永远拉不到第二段）；歌手条目点行进**歌手页**，用户条目进用户页。
粉丝名单单源。`CanOpenRelations` 对 QQ 要求 `IsLoggedIn`（匿名读不到）；禁用态特意把模板背景
清成透明，否则会多出一块灰底。

### 4.1 关注/取关歌手的写接口（2026-11-11，歌手页关注按钮）

协议坐实路径：README 列的 `L-1124/QQMusicApi` 只有只读名单，写动作在
`multiPlatformMusicApi` / `Anomi-oo/wow-origin` 的 `platforms/qqmusic/module/artist_sub.js`；
本机 `D:\Software\QQMusic\QQMusic_Protocol.dll` 里同时能搜到 `Concern.ConcernSystemServer.cgi_concern_user`、
`music.concern.ConcernSystem.cgi_get_all_concern_list` 和 `usertype / userid / userinfo / opertype`
字段串，三者互相印证。

**QQ**（`musicu.fcg`，需登录；`SendMusicuMultiAsync` 已在 URL 附 `uin/qm_keyst/g_tk`，
正好满足参考实现的鉴权要求）：

```json
{"req_0":{"module":"music.concern.ConcernSystem","method":"cgi_concern_user_v2",
  "param":{"bussinesstype":"","source":137,"bussinessid":"","opertype":0,
           "userinfo":{"usertype":1,"userid":"<singerMid>"}}}}
```

- `opertype` 0 关注 / 1 取消；`usertype=1` 表示歌手（0 是用户）；`userid` 传 **singerMid**。
- 成功判据：顶层 `code=0`、`req_0.code=0`，`req_0.data.retCode/RetCode/code` 若存在必须为 0。
- 老模块 `Concern.ConcernSystemServer` 同方法也可用（`source=0` + `encrypt_singerid=1`），
  实现里作为业务码非零时的回退；关注/取关幂等，重复同操作无害。
- 关注态读 `GetFollowSingersAsync("")`（`HostUin` 留空 = 自己），按 `SingerMid` 翻页找。

**网易云**：明文 form POST `/api/artist/sub` 与 `/api/artist/unsub`
（**两个独立路径，不是某些老文档写的 `t=1/2`**），表单 `artistId=<id>` + `artistIds=[<id>]` +
`csrf_token`（`PostPlainWriteAsync` 统一补）。关注态读 `/api/artist/sublist?limit=200&offset=N&total=true`。
本机 WAF 丢弃 weapi 加密写请求，与创建/收藏歌单一样直接走明文。

**加载态**：点击瞬间 `ToggleFollowAsync` 置 `IsFollowBusy=true`，乐观翻转 `IsFollowed`
（文案立刻变成目标态"已关注"）、`CanFollow=false` ⇒ 按钮禁用并整体降到 0.6 透明度。
**不在图标位放进度环**：2026-11-11 先后试过「单独短弧（漏写 `StrokeDashArray` 被画成整圆环）」
和「静态淡轨道环 + 旋转短弧（Fluent 2 ProgressRing 的 track + indicator）」，用户最终确认不要，
改成图标保持显示乐观更新后的 `+` / `✓`，只用禁用 + 变暗反馈。`IsFollowBusy` 仍在 VM 中驱动
`CanFollow`（防重复点击），只是不再绑到视图。**别再把进度环加回这个按钮。**

⚠ **按钮宽度必须跟随内容，别再给文案固定盒子**（2026-11-11 二次修正）：中间版本为了让两态图标位
重合，给 `TextBlock.follow-label` 固定 `Width=50` + `MinWidth=100`，结果「关注」(2 字 28px) 塞进
50px 盒、`TextAlignment=Left` 又把空白全甩到右边 ⇒ **按钮右侧凭空多出 22px 留白**，视觉上像按钮
没对齐（探针截图一眼看出）。Fluent 2 Button 本身是内容自适应宽度，正解是**三个都不设**
（`MinWidth` / 文案盒 `Width` / `TextAlignment`）：内容 = 16 图标 + 6 间距 + 文本，左右留白各等于
12px 内边距 + 1px 描边。实测未关注 `76×36`、已关注 `90×36`，两态 `图标位X=13.0`、`右留白=13.0`
（`--artist-follow` 直接断言左右对称）。宽度变化 14px 因为按钮组右对齐，只让左边缘伸缩，
与「更多」按钮的 10px 间距不变；**图标相对按钮左侧恒为 13px，不再是「内容居中后漂移」**。

⚠ **等宽方案里的文字必须左对齐**（历史结论，仅当将来又回到固定盒方案时才需要）：固定宽度 +
`TextAlignment=Center` 会让短文案「关注」在盒左侧空出 11px，叠加 16px 图标盒侧边 3px 与
`Spacing=8` ⇒ 加号到文字**墨迹间距 23px**，远超 Fluent 2 图标文字间距档
（`spacingHorizontalXS=4` / `SNudge=6` / `S=8`）。项目统一 `Spacing=6`（同 play-all / 心动模式）。
注意 `Spacing` 是**盒到盒**，字形在 16px 图标盒内还有约 3px 侧边距，别拿规范值直接对墨迹。

**图标改用字体字形（2026-11-11）**：原先 `+` / `✓` 是自画 `Path` 几何描边，现换成
`FluentUISystemIcons.ttf` 原版字体的 `IconAdd`(`E92B`) / `IconCheck`(`E92C`)，
`FontFamily=LyricEaseIconsOriginal`、`FontSize=16`、图标位 16×16。该字体 `hhea`
行盒恰好 1em、墨迹在 em 盒内居中（水平偏差 <0.3px、垂直 <0.35px @16px），
不用补 Margin 就能稳稳居中。三套图标字体都没有 spinner / ProgressRing 字形
（官方 Fluent 也没有，它是控件不是图标）——这正是当时进度环只能用几何的原因。
增加 `IconCheck` 后按 `docs/icon-fonts.md` §六重跑了 `scripts/subset-icons.py`（需 fonttools），
两套子集各 +300B，`--check` 通过。

**未实测项**：两个写接口都没用真实账号跑过（会改动用户关注列表），只做了协议级核对、
编译和 `--artist-follow` UI 探针。首次实机点击若失败，VM 会乐观回滚，不会留下假的"已关注"。

## 5. QQ 隐私歌单（dir_show=2，2026-11 实测）

**现象**：他人主页能列出对方设为私密的歌单（标题、标称曲数、播放量都在），点进去一首歌都没有，
且没有任何提示 —— 看起来像"这个歌单是空的"或"加载失败"。同一用户的其他歌单正常。

**服务端行为**（`uniform_get_Dissinfo`，2026-11-06 用 Elilia 真机验证）：

| 字段 | 公开歌单 | 隐私歌单 |
|---|---|---|
| `req_0.code` / `data.code` | 0 / 0 | **0 / 0**（不报错）|
| `data.songlist` | N 首 | **空数组** |
| `data.total_song_num` | 真实曲数 | **照旧下发标称数**（如 204）|
| `dirinfo.dir_show` | 1 | **2** |
| `dirinfo.show` / `songnum` | 1 / 真实 | 0 / **0** |
| `dirinfo.title` | 歌单名 | **"歌单被主人设为隐私"**（服务端本地化文案，不可靠）|

`enc_host_uin` 传歌单主人的 encrypt_uin **没有用**（实测同样返回空 songlist）—— 非本人就是读不到，
这是权限而不是参数问题。老接口 `CgiGetDiss` 回 code=0 但 songlist 空，同样白屏。

**修法**（三处，缺一处用户就看不懂发生了什么）：

1. `QQMusicApiClient.GetPlaylistTrackPageAsync`：`dirinfo.dir_show == 2` → 抛
   `ApiException("该歌单被主人设为隐私，仅本人可见")`；再加一条兜底 —— 首页 `songlist` 空而
   `total_song_num > 0` 时抛"这 N 首暂不可见"（全下架也是这个形态）。
2. 主页 `mydiss.list` 的条目**自带 `dir_show`**（和详情接口一致）→ `Playlist.IsPrivate` →
   `PlaylistItemViewModel.TrackCountText` 显示"隐私歌单"而不是"N 首"，卡片与歌单页 hero 都跟着变。
3. 歌单页原本没有空态文案（`Message` 在 `PlaylistView` 里根本没绑定），失败=空白 →
   `PlaylistViewModel.EmptyTracksHint` + 卡片第三行居中提示；`ClearTrackRows()` 里清空，
   QQ 路径加载完/失败时填。

⚠ 不要用 `dirinfo.title` 判断：那是界面文案，随语言/版本变；`dir_show` 才是标记位。

## 6. 回归

```powershell
dotnet run --project src/ALyricEase.Headless/ALyricEase.Headless.csproj -c Debug -- --user-relations
```

假 JSON 走真实客户端路径，覆盖：加载态转圈可见且盖住内容（`profile-loading.png` /
`profile-loading-dark.png`）→ 解析 → 合并入口进入「关注与粉丝」→ 首屏 30 条 →
切到粉丝 Tab 拉到 8 条并切回保留 30 条（`relations-followers.png`）→ 滚到底补到 45 →
窄屏无溢出 → 点行进对方用户页 → 返回恢复 45 条 + 当前 Tab + 滚动 240 → 再返回恢复原用户页 →
QQ 登录后合并入口可点、关注名单 2 用户 + 1 歌手拼成一份、切粉丝 Tab、点歌手进歌手页、
点用户进用户页；自己的 QQ 页计数非 0（`hero-qq-self.png`）且不继承上一个人的 euin。
截图落在临时目录（末尾会打印路径）。

```powershell
# 联网冒烟（只读）：网易云两个端点 + QQ 三个 method + 两种用户页计数（QQ 未登录会跳过）
dotnet run --project src/ALyricEase.Headless/ALyricEase.Headless.csproj -c Debug -- --user-relations-live [uid]
```

```powershell
# 他人主页歌单逐个试读：列出喜欢/自建/收藏每个歌单的 dir_show 与首页读取结果
# （参数可给昵称，会在自己的粉丝名单里找；也可直接给 encrypt_uin）
dotnet run --project src/ALyricEase.Headless/ALyricEase.Headless.csproj -c Debug -- --qq-user-pl Elilia
# 单个歌单走生产打开路径：打印行数 / Message / 空态提示
dotnet run --project src/ALyricEase.Headless/ALyricEase.Headless.csproj -c Debug -- --qqopen 9160613043 13 BGM
```
