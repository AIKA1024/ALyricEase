# 文档索引

本目录放**实测结论与踩坑记录**,是这些结论的权威出处;
仓库根的 `.workbuddy/memory/MEMORY.md`(**本地工作笔记,未入仓**)受注入长度限制,只留摘要与指向这里的入口,加内容前先问"能不能压进 docs";
每日过程记录写 `.workbuddy/memory/YYYY-MM-DD.md`(同上,本地)。

**约定**:新内容**追加到对应文档末尾**;推翻旧结论时**就地改并标注日期 + "已结案 / 已回退"**,不要留着两套说法;
新增/重命名文档时必须同步本索引(改了链接记得点一遍)。

---

## 一、按"我要解决的问题"找

| 你遇到的情况 | 去哪看 |
|---|---|
| 控件位置不对 / 样式不生效 / 改了一条样式波及别处 | [avalonia-tips.md](avalonia-tips.md)「布局与外观一律走 XAML」 |
| 小屏适配、断点切换"改一半" | [avalonia-tips.md](avalonia-tips.md)「容器查询」小节 |
| 页面往返内存一直涨 / 返回后空白一下 | [memory-audit.md](memory-audit.md);[avalonia-tips.md](avalonia-tips.md)「页面往返的内存/生命周期回归」「详情页返回卡顿」 |
| 打开另一个歌单时行封面"闪一下" | [avalonia-tips.md](avalonia-tips.md)「改集合就会回收行容器」 |
| 某处 GPU 占用偏高("6% 能降吗") | [perf-notes.md](perf-notes.md)(先读「详情页 GPU 的大头是"在动"」) |
| 歌词面板太贵 / 逐行模糊该不该留 | [perf-notes.md](perf-notes.md)(先读「把歌词模糊收进『性能与体验』档位」;再读它上面那条已回退的「摘掉远景行的模糊」——**为什么试过又撤**) |
| 某个动画/循环最小化后还在烧 CPU | [avalonia-tips.md](avalonia-tips.md)「自续订的 RequestAnimationFrame 循环」「先分清三类"一直在动"」 |
| 进度条/动画"卡卡的,鼠标一动就顺" | [avalonia-tips.md](avalonia-tips.md) 文末「UI 线程静息时长睡 ~79ms」 |
| Release 包冷启动就崩 / 构建期 IL2072 | [avalonia-tips.md](avalonia-tips.md)「Release 包的裁剪会删掉…」;[android-notes.md](android-notes.md) §6 |
| Android 返回键 / 清单改了不生效 / 图片内存预算 | [android-notes.md](android-notes.md) |
| 要改曲目行的样式 | [original-track-row-styles.md](original-track-row-styles.md)(改完对照 [TrackRow.axaml](../src/ALyricEase/Views/TrackRow.axaml)) |
| 要加或改图标码位 | [icon-fonts.md](icon-fonts.md) |
| 登录相关(代理登录 / MUSIC_U / eapi 两种格式) | [netease-login.md](netease-login.md) |
| 要加网易云端点 / 排查 400 空响应 / 防封号 | [netease-api/](netease-api/)(先读它的 README,再过 [03-antiban.md](netease-api/03-antiban.md) 的检查清单) |
| 自问"原版 UWP 为什么占用更低" | [original-uwp-notes.md](original-uwp-notes.md)(账记在 `dwm.exe` 里,别承诺做到同读数) |
| 要写或改性能探针 | [perf-notes.md](perf-notes.md) §四「写性能探针的坑」+ §七探针清单 |

---

## 二、框架与 UI

[**`avalonia-tips.md`**](avalonia-tips.md) —— 本目录最大的一份,按主题累积:

- 事件语义(`Tapped`/`Click` vs 手拼 pressed+released、ListBox 行选中时机、Tunnel|Bubble 抢占)
- 样式优先级与"局部值"陷阱、容器查询做响应式、`Button` 内容对齐、`Flyout` 开启动画
- 列表虚拟化(横向 `VirtualizingStackPanel` 不能换)、行容器回收与封面重解
- 页面生命周期与内存回归的测法(别用任务管理器肉眼看)
- GPU 归因口径、"会每帧在变"的控件不能挂 `Effect`
- 自续订 RAF 循环的门控(三类"一直在动"的成本模型不同)
- 帧供给断供:UI 线程静息长睡 + `UiFramePacer`
- 踩坑:改完先看构建产物时间戳再谈读数

## 三、性能

[**`perf-notes.md`**](perf-notes.md) —— 怎么量 GPU(PDH `\GPU Engine(*)\Utilization Percentage` 口径与"被遮挡时恒 0"的陷阱)、
详情页 GPU 的大头归因、**已落地**的改动(歌词模糊收进设置里的「性能与体验」档位:
最佳性能整档不挂 `Effect`,省 **0.95% GPU**,视觉代价平均通道差 0.26 级)、
**试过但判负**的画法(定时器驱动、预渲染纹理、`BitmapCache`、按距离摘掉远景行的模糊)、
消融夹具的三条铁律(停/起只走 `MotionEnabled`;变体不许与轮内位置绑定,用拉丁方;
逐行消融写的是本地值,会污染后面所有档)、探针清单。

## 四、内存

[**`memory-audit.md`**](memory-audit.md) —— 五层内存结构(图片解码 / 页面数据 / 列表视图 / 播放队列 / 磁盘缓存)、
预算总账、P1~P3 问题清单**与处置状态**(已实施项标 ✅ 并就地更新为当前实现)、真机自检入口。

## 五、Android

[**`android-notes.md`**](android-notes.md) —— 生效的清单只有哪一份、返回键定稿做法(别退回旧写法)、`#if ANDROID` 在核心库是死分支、
图片解码预算、真机自检入口、只有 Release 才暴露的两个问题、沙箱里做 Release 全量构建。

## 六、第三方 API 逆向

- [**`netease-login.md`**](netease-login.md) —— 项目侧登录链路:两种登录方式、代理登录(**仅 Windows**,走 NuGet `ProjectCirrus.Copycat`)、
  为什么能 MITM 与"登录成功后必须提醒关客户端代理"、eapi 私有变体 ≠ 标准客户端格式。
- [**`netease-api/`**](netease-api/) —— 网易云 Web API 全集(**子目录自带 [README](netease-api/README.md) 索引**):
  [01-crypto.md](netease-api/01-crypto.md) 加密逐字节拆解 · [02-endpoints.md](netease-api/02-endpoints.md) 端点目录 ·
  [03-antiban.md](netease-api/03-antiban.md) 反封号与降级链 · [04-verify-selftest.md](netease-api/04-verify-selftest.md) 端到端验证清单(`--selftest`)。
  一切以 `Services/NetEase/` 下的代码为准。

## 七、原版 LyricEase(UWP) 参考

来源是 `C:\Users\AIKA\Downloads\DUMP\` 里的解包 + 反编译产物。

- [**`original-uwp-notes.md`**](original-uwp-notes.md) —— 为什么原版读数更低(活儿跑在 `dwm.exe`,不是画得更省)、
  哪些能抄哪些抄不到、照抄前要先确认依赖的是不是系统合成器。
- [**`original-track-row-styles.md`**](original-track-row-styles.md) —— 曲目行(`TrackListItem`)样式原值、UWP SystemBase 色值、
  与当前实现(`TrackRow.axaml`)的对照。

## 八、资源

[**`icon-fonts.md`**](icon-fonts.md) —— 三套图标字体的来源/分工/码位映射、`Icons.axaml` 之外的码位字面量(改码位必须同步)、
维护规则(踩坑记录)、发布期子集化(TODO)、验证方法。

仓库根的 [**`THIRD-PARTY-NOTICES.md`**](../THIRD-PARTY-NOTICES.md) —— 第三方许可声明(`AsyncImageLoader.Avalonia`;
QQ 音乐 native 扫码登录协议改编自 `yakult-green-tea/qq-music-api`(MIT);
QQ 音乐 API 客户端(`QQMusicApiClient`)上游协议参考 `L-1124/QQMusicApi`(GPL-3.0))。
新增/替换第三方依赖或改编外部实现时**同步这里**。

---

## 九、探针

性能与内存类的结论几乎都有对应的 Headless 探针,统一注册在
[`src/ALyricEase.Headless/Program.cs`](../src/ALyricEase.Headless/Program.cs);
清单、用途与**回归判据**见 [perf-notes.md](perf-notes.md) §七那张表(`--sgbottom`、`--pl-cover-flash`、`--accountlike` 等)。
**加档改那里**,不要另起入口 —— 结论要能被复现,不能只留数字。
