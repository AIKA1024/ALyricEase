# 原版 LyricEase 曲目行样式参考(TrackListItem)

> 来源:反编译产物 `C:\Users\AIKA\Downloads\DUMP\shell_xaml\TrackListItem.xaml` + `Buttons.xaml`。
> 每次改行样式前先看这里,不要凭印象。改完对照实现(TrackRow.axaml)。

## 所有 TrackListItem 样式共有的

| 项 | 值 | 出处 |
|---|---|---|
| RootGrid | `Height="60" Padding="8,0" ColumnSpacing="8"` | 每套样式第一行 |
| 行背景 Normal | `#00FFFFFF`(透明) | `ColorAnimation To="#00FFFFFF"` |
| 行背景 **PointerOver** | `{ThemeResource SystemBaseLowColor}` | `ColorAnimation` |
| 行背景 **Pressed** | `{ThemeResource SystemBaseMediumLowColor}` | `ColorAnimation` |
| 背景过渡 | 150ms(`Duration="0:0:0.150"`) | `ColorAnimation` |
| 封面 | `CornerRadius="4"` `Margin="0,5"` `Stretch="UniformToFill"` | |
| 播放按钮 | `32×32` `CornerRadius="16"` `Padding="0"`,图标 `FluentFontIconPlayFilled` **FontSize 16**,悬停浮现(默认 UWP Button hover/pressed = BaseLow/MediumLow) | |
| 更多按钮 | `32×32` `CornerRadius="16"` `Padding="0"`,图标 `FluentFontIconMore` **FontSize 16**,悬停浮现 | |
| 红心按钮 | `40×40` `CornerRadius="20"` **FontSize 24**,封面居中,悬停浮现 | |
| 时长 | `FontSize="12"` 同列居中,悬停时收起被更多按钮盖住 | |
| 歌手/专辑文本 | `FontSize="12"` `Foreground=SystemBaseMediumColor` `TextTrimming=CharacterEllipsis` `CharacterSpacing=-20` | |
| 序号 | `FontSize="12"` 居中 `Foreground=SystemBaseMediumLowColor` | |
| 标题 | 无显式字号/粗细(默认 14 normal),`TextTrimming=CharacterEllipsis`;`ColumnSpan=2` 占播放列,悬停收起为 1 | |

**悬停浮现(OperationGridVisible)**:PlayButton / MoreButton / LikeToggleButton 可见,时长 `Collapsed`,标题 `ColumnSpan → 1`。

## UWP SystemBase 色值(亮/暗)

| 资源 | Light | Dark |
|---|---|---|
| SystemBaseLowColor | `#33000000` | `#33FFFFFF` |
| SystemBaseMediumLowColor | `#59000000` | `#59FFFFFF` |
| SystemBaseMediumColor | `#66000000` | `#66FFFFFF` |

## 六套样式

| 样式键 | 列定义 | 备注 |
|---|---|---|
| `SearchResultTrackListITemStyle` | `50 \| * \| 32 \| 32` | **无序号**。封面+红心 / 标题+歌手(span2) / 播放 / 时长+更多 |
| `PlaylistTrackListItemWideStyle` | `30 \| 50 \| 3* \| 32 \| 2* \| 3* \| 32` | 序号 / 封面+红心 / **歌名**(span2) / **播放** / **歌手** / **专辑** / 时长+更多 |
| `PlaylistTrackListItemCompactStyle` | `30 \| 50 \| * \| 32 \| 32` | 序号 / 封面+红心 / 标题(span2) / 播放 / 时长+更多 |
| `PlaybackRecordTrackListItemStyle` | `30 \| 50 \| * \| 32 \| 32` | 同上 |
| `ArtistHotTrackListItemStyle` | `50 \| * \| 32 \| 32` | 无序号 |
| `AlbumTrackListItemStyle` | `50 \| * \| 32 \| 32` | 无序号;红心 `32×32 FontSize 18` |

## LikeToggleButtonStyle(Buttons.xaml:584-680)

模板:`Border Background="#FF000000"`(纯黑圆,尺寸/圆角 TemplateBinding),子元素实心 `FluentFontIconHeart`(U+E931) `Foreground="#FFFFFFFF"`。

| 状态 | 爱心颜色 | 说明 |
|---|---|---|
| Normal / PointerOver | `#FFFFFFFF`(白) | **悬停无变化** |
| Pressed | `#FFF5B7B1`(浅粉) | 按下变浅 |
| Disabled | `#FF777777` | |
| Checked / CheckedPointerOver | `#FFE74C3C`(红) | 选中红;选中悬停无变化 |
| CheckedPressed | `#FFB03A2E`(深红) | |

**结论:红心无 hover 样式,只有按下变浅、选中变红。** 背景始终纯黑圆。

## 播放/更多按钮

原版就是普通 `<Button>`(UWP 默认样式)→ hover = BaseLow、pressed = MediumLow。图标:
- 播放 `FluentFontIconPlayFilled` = U+E912(= 我们的 `IconPlayFilled`)
- 更多 `FluentFontIconMore` = U+E909(= 我们的 `IconMore`,但行内用的是文字 `•••`)

## 当前实现对照(TrackRow.axaml)

- 行悬停高亮:`row-hover-bg` 画在**内容之后**(背景色,仿原版 Grid.Background 动画),`SystemBaseLow`(20%),按下 `SystemBaseMediumLow`(35%),150ms Opacity 淡入,圆角 4(仿原版 ListView/GridViewItemPresenter);不遮罩封面/红心。
- 播放/更多:`32×32` 圆,透明底,悬停 `SystemBaseLow`、按下 `SystemBaseMediumLow`(专用 RowButtonTheme,无 Fluent 默认悬停)。
- 歌手/专辑:可点击按钮(仿原版 Artists/AlbumButton),`Background #00FFFFFF CornerRadius 4 Padding 4`,悬停 `SystemBaseLow`、按下 `SystemBaseMediumLow`;点击跳歌手页/专辑页。
- 红心:纯黑圆 `#FF000000` + 实心爱心,白/红 E74C3C,无 hover,按下 F5B7B1、选中 E74C3C。
- 时长 FontSize 12 居中,更多按钮同列盖住。
