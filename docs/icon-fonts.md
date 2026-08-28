# 图标字体说明

> 记录项目三套图标字体的来源、分工、码位映射与维护规则。2026-08 图标体系重构（原版字体回归 + Fluent 字体补位）后整理。
> 图标资源统一定义在 `src/ALyricEase/Styles/Foundation/Icons.axaml`；字体文件在 `src/ALyricEase/Assets/Fonts/`（csproj 以 `Assets\**` 通配打包）。

## 一、字体文件与资源键

| 文件 | 字体家族 | 来源 | 码位范围 | 资源键 | 用途 |
|---|---|---|---|---|---|
| `FluentUISystemIcons.ttf` | `FluentUISystemIcons` | 原版 UWP LyricEase 内嵌字体 + 本项目扩展 | `E900–E95F`（94 个） | `LyricEaseIconsOriginal` | 侧边栏、歌曲行菜单、标题栏等须与原版像素级一致的部位 |
| `FluentSystemIcons-Filled.ttf` | `FluentSystemIcons-Filled` | [microsoft/fluentui-system-icons](https://github.com/microsoft/fluentui-system-icons) 官方完整版 | PUA 区离散分布 | `LyricEaseIcons`（主）、`LyricEaseIconsFilled`（别名） | 其余全部图标（播放控制、红心、设置页等）的默认字体 |
| `FluentSystemIcons-Regular.ttf` | `FluentSystemIcons-Regular` | 同上 Regular 字重 | 同上 | `LyricEaseIconsOutline` | 少数细线字形：歌词、播放列表、图钉、空心红心 |

**原版字体来历**：从原版 UWP 安装包解包产物的 `LyricEase.Shell/UIAssets/FontIcons/FluentUISystemIcons.ttf` 取得（原版映射表为其 `shell_xaml/FontIcons.xaml`，80 项，`E900–E94F`）。本项目在此基础上扩展了 `E950–E959`（标题栏 5 键 + 播放模式 4 键，轮廓取自 Segoe MDL2 以对齐原版观感），形成现在的 94 码位版本——它同时是 dump 字体的严格超集，替换时务必用扩展版。

## 二、原版字体码位表（`FluentUISystemIcons.ttf`）

`E900–E94F` 来自原版 `FontIcons.xaml`（名字即原版资源键，去掉 `FluentFontIcon` 前缀）：

| 码位 | 名称 | 码位 | 名称 | 码位 | 名称 | 码位 | 名称 |
|---|---|---|---|---|---|---|---|
| E900 | BackToTop | E914 | Previous | E928 | Feedback | E93C | QuestionMark |
| E901 | ChevronDown | E915 | Search | E929 | MicrosoftStore | E93D | Edit |
| E902 | CopyLink | E916 | SearchSquare | E92A | Library | E93E | Unpin |
| E903 | Home | E917 | Settings | E92B | Add | E93F | AudioCrossfading |
| E904 | ListFilled | E918 | Share | E92C | Check | E940 | Code |
| E905 | ListRegular | E919 | Volume0 | E92D | Swap | E941 | Specifications |
| E906 | LyricsFilled | E91A | Volume1 | E92E | NumberList | E942 | Performance |
| E907 | LyricsRegular | E91B | Volume2 | E92F | PlaybackRecord | E943 | GitHub |
| E908 | Mention | E91C | TextFontSize | E930 | PlaybackRecordFilled | E944 | HeartPulse |
| E909 | More | E91D | Translation | E931 | Heart | E945 | Radio |
| E90A | MoreCircle | E91E | Play | E932 | Delete | E946 | Windows10 |
| E90B | MusicNote | E91F | QuestionFilled | E933 | ContentGallery | E947 | Windows11 |
| E90C | MusicNotes | E920 | MultiArtists | E934 | CloudFilled | E948 | SpaceBar |
| E90D | Navigate | E921 | SingleArtist | E935 | Recommend | E949 | HeartPulseRegular |
| E90E | Next | E922 | Album | E936 | GlobeFilled | E94A | HeartRegular |
| E90F | Pause | E923 | ExitFullScreen | E937 | ClockFilled | E94B | Disc |
| E910 | Person | E924 | EnterFullScreen | E938 | UpdateFilled | E94C | LastFm |
| E911 | Pin | E925 | Lock | E939 | SignOutFilled | E94D | ArrowExit |
| E912 | PlayFilled | E926 | Comment | E93A | EraserFilled | E94E | LinkSquare |
| E913 | PlayNext | E927 | Mail | E93B | MultiplePersonsFilled | E94F | Open |

本项目扩展（Segoe MDL2 描摹）：

| 码位 | 名称 | 码位 | 名称 |
|---|---|---|---|
| E950 | Back（标题栏返回） | E956 | RepeatList（列表循环） |
| E951 | WindowMinimize | E957 | RepeatOne（单曲循环） |
| E952 | WindowMaximize | E958 | Shuffle（随机） |
| E953 | WindowRestore | E959 | Pin（固定） |
| E954 | Close | | |

## 三、Icons.axaml 键的字体归属

- **`LyricEaseIconsOriginal`（原版字体）**：`IconPerson` `IconSettings` `IconCode` `IconChevronDown`（侧边栏与折叠箭头）、`IconSingleArtist` `IconMultiArtists` `IconAlbum`（歌曲行菜单）、`IconBack` `IconWindowMinimize` `IconWindowMaximize` `IconWindowRestore` `IconClose`（标题栏）。导航项图标不经键，直接是 `MainViewModel` 里的字面量（见下）。
- **`LyricEaseIconsOutline`（Regular 细线）**：`IconLyrics`（slide_text_24_regular `F6EC`）、`IconPlaylist`（text_bullet_list_square_24_regular `F7A9`）、`IconPin`（dismiss_28_regular `F36B`，旧版"pin"本就是 X 形）、`IconHeartOutline`（空心红心 `F47A`，PlayerBar 未红心态）。
- **`LyricEaseIcons`（Filled，默认）**：其余全部键。播放控制用 `LyricEaseIconsFilled` 别名引用的 TextBlock 与之等价。

## 四、Icons.axaml 之外的码位字面量（改码位必须同步）

| 位置 | 内容 | 渲染字体 |
|---|---|---|
| `ViewModels/MainViewModel.cs` | 导航项 IconGlyph：E915/E935/E936/E945/E92A/E934/E937 | `LyricEaseIconsOriginal`（视图模板指定） |
| `ViewModels/PlayerViewModel.cs` | PlaybackModeGlyph：F172 列表循环 / EF34 单曲循环 / EF37 随机 / E70F 心动模式 | `LyricEaseIcons`（Filled） |
| `Views/NowPlayingView.axaml.cs` | 全屏切换 `\uE690` 进 / `\uE693` 出 | `LyricEaseIcons`（Filled） |
| `Desktop/MainWindow.axaml.cs` | 最大化/还原切换：E952 / E953 | `LyricEaseIconsOriginal`（MaxGlyph） |
| `Desktop/Services/Taskbar/TaskbarThumbButtons.cs` | `MediaShape`：F633/F610/F5AC/F574（上一曲/播放/暂停/下一曲） | 自加载 `FluentSystemIcons-Filled.ttf` |

## 五、维护规则（踩坑记录，勿重蹈）

1. **码位不要加前导 E**。Fluent 官方码位本身已在 PUA 区（`EExx–F0xxx`，如 search=`F690`）。曾因按旧 E9xx 习惯补 `E` 导致 53 个图标全表错乱（`F690`→`EF690` 落进空洞）。
2. **同名图标在 Regular/Filled 两个字体的码位可能不同**（search：R=`F690` / F=`F69A`；heart：R=`F47A` / F=`F47E`），也有相同的（chevron、shuffle、add 等）。改码位必须按 glyph 名从**目标字体**的 cmap/post 表解析，不能凭记忆手抄。
3. **键换字体时，所有消费方的 `FontFamily` 必须同步切换**。码位是字体私有的：曾出现 `IconChevronDown`（=E901）仍被 Filled 字体渲染成了别的字形（E901 在 Filled 里是另一个图标）。
4. **字面量与 Icons.axaml 保持同值**。批量替换时替换映射要按"当前值"写，静默 no-op 会留下旧码位（发生过一次导航字面量漏改）。
5. **`LyricEaseIconsFilled` 与 `LyricEaseIcons` 目前指向同一字体**，保留两个键是为了语义清晰；若将来分化需重查全部消费方。

## 六、发布期子集化（TODO）

三套字体按码位裁剪以缩小体积（官方两套完整字体各 ~2.5MB）：

- 码位清单 = Icons.axaml 全部键 + 第四节字面量（当前同值），按字体分组：
  - Filled：除 Outline/Original 归属外的全部键值；
  - Regular：`F6EC` `F7A9` `F36B` `F47A`；
  - 原版字体：建议整包保留（仅 19KB，无需子集化）。
- 用 `pyftsubset`：`--unicodes` 显式传入清单，**保留 name 表**（家族名 `FluentSystemIcons-Filled/Regular` 不能变，否则 `#` 引用断链全线回退）与 hinting（小字号栅格化不变）。
- 子集化后必须重跑验证：解析子集字体 cmap，断言清单内码位全部在场。

## 七、验证方法

图标码位的正确性可用 Python 直接解析 TTF 验证（本仓库多次使用）：

- `cmap`（format 4/12）拿 码位→glyphID；`post`（format 2.0）拿 glyphID→glyph 名（如 `ic_fluent_search_24_regular`）。
- 断言：Icons.axaml 每个键的码位 ∈ 其归属字体的 cmap；视图/VM 字面量同查；`{DynamicResource IconX}` 的消费方 `FontFamily` 与键归属一致。
- 形状比对（可选）：PIL `ImageFont` 渲染字形掩码，与旧字形做 IoU——本次 Filled 字形选择即按此法逐个匹配旧版观感。
