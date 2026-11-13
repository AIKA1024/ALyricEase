# 专辑卡(歌手页 / 全部专辑页)

用户反馈:歌手页专辑区、以及"查看更多"进入的专辑页,卡片只有"点进专辑页"一个动作——
没有悬停 ▶、没有 •••、右键/长按也没有菜单。浏览页「关注新专」的卡片这些都有。

---

## 1. 做法:抽成一个共用控件

新建 `Controls/AlbumCard.axaml`(+ code-behind),歌手页(`ArtistView.axaml`)与全部专辑页
(`Controls/AlbumGrid.axaml`)的 `DataTemplate` 都只剩一行:

```xml
<DataTemplate x:DataType="vm:AlbumCardViewModel">
  <controls:AlbumCard />
</DataTemplate>
```

控件内部:

- 外层 `Button.album-card` 仍绑 `OpenCommand`(点卡片其它位置 = 进专辑页);
- 封面 Grid 上叠一层 `Border.album-card-mask`(hover 黑遮罩 0→0.2)与两枚 `Button.card-op`:
  左下 ▶(`PlayCommand`)、右下 •••(`OnMoreClick`);
- 根 Button 挂 `ContextRequested`(桌面右键 / 触屏长按)→ 与 ••• 同一个菜单。

样式(`album-card` 透明底、遮罩、`card-op` 32×32 圆形、`:pointerover` 时 Opacity 0→1)
全部搬进控件自己的 `UserControl.Styles`,页面里原来那三份重复样式已删除。
**显隐用 Opacity 不用 IsVisible**:不做布局反馈,不会触发 over 链重算的闪烁。

## 2. 曲目按需拉(卡片 VM 缓存)

▶ 与菜单都要"整张专辑的曲目",但卡片是列表里批量出现的,不能一上来就 N 次请求。
`AlbumCardViewModel` 加:

- `LoadSongsAsync()`:网易云 `GetAlbumAsync(id)` / QQ `GetAlbumSongsByMidAsync(mid, 200)`,
  成功即缓存,失败返回空(下次点击可重试),并发调用共享同一个在途 `Task`(单飞);
- `PlayCommand`:`PlayFromList(songs[0], songs, Title)` 播整张;
- 拉不到曲目时 **不弹菜单**(没有可播/可跳内容),也不播放。

菜单复用浏览页那份 `AlbumCardContextMenu`,签名从"吃 `BrowseCardViewModel`"改成
`Create(anchor, title, songs, playAction)` —— 浏览页与专辑卡从此共用一套
(专辑名 / 播放（N 首）/ 打开专辑页 / 表演者 / ─ / 分享 / 复制链接)。

## 3. 三个坑

- **样式搬进控件后,必须把所有内联模板都换掉**(实际踩到):歌手页有两份一模一样的卡片模板
  (「专辑」区 + 「单曲与EP」区),只换了前者 ⇒ 后者残留 `Border.album-card-mask` 却已没有
  `Opacity=0` 的默认值,遮罩按默认 1 渲染,整个「单曲与EP」区封面全黑。
  这是"删样式"的隐性代价:**丢的不是报错,是默认值**。改样式前先全仓 grep 该 class 的所有使用者
  (`grep -rn 'album-card-mask'`),一处都不能留。
- **探针别取"第一个 TextBlock"当标题**:卡片里现在有 ▶/••• 的图标 `TextBlock`,
  视觉树顺序排在标题之前,`AlbumGridProbe` 一开始就抓到了播放图标(标题 y 变成 168、判成"重叠 -32")。
  改成按文案挑(`t.Text.StartsWith("专辑")`)后恢复正常(间距 6)。
- **`UserControl` 在横向 `StackPanel` 里显式 `VerticalAlignment="Top"`**:否则默认 Stretch
  会被父容器高度带着走。实测本页几何没变,但这是同类封装的通用前提。

## 4. 回归

```bash
--album-card     # 真实 QQ 专辑:2 枚操作按钮 / 曲目能拉 / 菜单 7 项结构完整
--albumgrid      # 卡片几何:封面 200 + 标题间距 6(全部专辑页与歌手页都验)
--album-display  # 专辑页列表/大图两种模式、虚拟化、曲数回填
--acards         # 竖切:单曲与EP 区是否走 AlbumCard、遮罩未悬停时是否全透明
```

`--acards` 新增的「单曲与EP」断言:填 40 张单曲后,该区 `AlbumCard` 控件数 == 遮罩数,
且未悬停时遮罩最大 `Opacity` 为 0;实测 6 张实化 / 6 个遮罩 / 最大透明度 0.00,通过。
黑封面这种"样式丢了但不报错"的故障,只能靠这类数值断言兜住。

`--album-card` 的判据:操作按钮恰好 2 枚(默认 Opacity 0)、`LoadSongsAsync` 返回 >0、
菜单含「播放（N 首）」与「打开专辑页」。实测《放课后时光》9 首、菜单 7 项。
