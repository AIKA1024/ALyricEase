# 原版 LyricEase(UWP)参考:为什么它的占用比我们低

> 数据来源:`C:\Users\AIKA\Downloads\DUMP\`(原版安装包解包 + 反编译产物)。
> 本文只记**能解释性能差异**的那部分;纯外观参考见 `original-track-row-styles.md`。
> 2026-09-21 整理。

## 一、先分清"账记在谁头上"

用户实测原版播放详情页进程 GPU **≈0.1%**,而我们在 1920×1080 下 **8~17%**。这不是
"原版画得更省",**大部分是测量口径**:

- UWP 的组合动画、`AcrylicBrush`、`BackdropBlurBrush`、`DropShadowPanel`
  (`Compositor.CreateDropShadow`) 全部由**系统合成器(`dwm.exe` 里的 `Windows.UI.Composition`)**执行,
  跑在 DWM 的独立动画线程上 ⇒ **不计入应用进程**。
- 我们的 Avalonia 版合成器在**应用进程内**(`Avalonia.Rendering.Composition` + Skia),
  同一件事的成本全额记在自己头上。

⇒ 想看总账,任务管理器里要把 **`dwm.exe`(桌面窗口管理器)** 的 GPU 一起读。
⇒ **不要承诺"把读数做到和原版一样"** —— 那部分它根本不在自己进程里付钱。

## 二、原版的实际写法(证据)

### 色团背景 `shell_xaml/AlbumCoverBackgroundControl.xaml`

```
Grid
├─ Grid StaticLayer            ← Visibility="Visible"
│   └─ Rectangle + SynchronizedAcrylicBrush(TintColor=#00FFFFFF, TintOpacity=0.6)
├─ Viewbox AnimationLayer      ← Visibility="Collapsed"  ← 动态背景默认关
│   └─ Viewbox.Child = Grid BackGrid  Width=1000 Height=1000, 自己 Clip 1000×1000
│       ├─ Canvas EllipseCanvas  (Clip 1000×1000, 椭圆由代码加)
│       ├─ Rectangle + BackdropBlurBrush Amount=100
│       └─ Rectangle + BackdropBlurBrush Amount=20
└─ Rectangle Fill=#FF000000 Opacity=0.4
```

三个关键点:

1. **色团画在固定 1000×1000 的画布里,外面套 `Viewbox Stretch="Fill"`** ⇒ 栅格化分辨率与窗口尺寸**解耦**。
   我们的版本是 10 个 `RadialGradientBrush` 椭圆**直接按窗口 1920×1080 画** —— 这也是为什么
   我们的读数随窗口涨(1200×720 → 1920×1080 会翻倍级)。
2. **默认 `Visibility="Collapsed"`** + 设置页 `DynaBackEnableCheckBox` ⇒ 动态背景默认关。
   量原版性能前先确认这个开关开着,否则量到的是"静止画面"。
3. 模糊是 `BackdropBlurBrush`(**合成器 backdrop 笔刷**),不是给元素挂 `Effect`。

### 全仓 `shell_xaml` 的 grep 结果(很强的一条)

`BlurEffect` / `Effect=` / `OpacityMask` / `CacheMode` —— **四个都零命中**。
原版从不用"给控件挂离屏效果"这条路,全部走合成器笔刷(`BackdropBlurBrush` /
`SynchronizedAcrylicBrush` / `DropShadowPanel`,后者是 `Compositor.CreateDropShadow`)。

### 歌词行 `shell_xaml/InteractiveLyricControl2.xaml`

```
Border BackdropBlurBrush Amount  ← 逐档:Present=0 / AbovePresent=1.5 / Below1=1 / …
StackPanel TextStackPanel Opacity ← 逐档:1(当前) / 0.7 / 0.8 / 0.4(远景)
Border TextBlurMask(叠在文字之上,内填 BackdropBlurBrush Amount=5,Opacity 可 0/1 切换)
```

- "模糊"是 `BackdropBlurBrush Amount` 的 VisualState 过渡,**当前句就是 0**(与我们改成
  `Effect="{x:Null}"` 的目标一致;区别是我们的 `BlurEffect Radius=0` 曾经一样贵,见 `perf-notes.md`)。
- ⚠ **`EnableDependentAnimation="True"`** 写在这些 `Amount` 动画上 —— 微软要求"跑不到合成线程的动画"
  显式开这个开关才允许。也就是说:动 brush 属性的那 0.3s 过渡,原版走的是 **UI 线程**,不是白来的。
- **边缘渐隐靠"逐行 `Opacity`",没有渐变 `OpacityMask`**。我们 `LyricView.axaml` 给
  `ListBox#LyricList` 挂了整块 `OpacityMask`(视口上下淡出)⇒ 整个列表走离屏。
  **可抄**:接受"逐行均匀透明"的观感,就能换成逐行 `Opacity`(合成动画,几乎零成本)。

### 动画模型 `shell_xaml/PlaybackDetailView.xaml`

```xml
<ImplicitAnimationSet x:Key="OffsetAnimations">
    <Vector3Animation Target="Offset" Duration="0:0:0.3" />
</ImplicitAnimationSet>
...
<FluentButton Animations="{StaticResource OffsetAnimations}" ...>
```

隐式**组合**动画(toolkit 的 `Microsoft.Toolkit.Uwp.UI.Animations`),由合成器执行。

`LyricEase.Core.dll` 二进制 grep 命中:`ElementCompositionPreview`、`GetElementVisual`、
`CreateVector3KeyFrameAnimation`、`ScalarKeyFrameAnimation`、`Compositor`、
`CompositionBackdropBrush`、`BackdropBlurBrush`、`XamlCompositionBrushBase`、`DropShadow`
⇒ 确认色团漂移走的是**独立组合动画**,不是 Storyboard。

## 三、结论:哪些能抄、哪些抄不到

| 原版做法 | Avalonia 有没有对等物 |
|---|---|
| 合成器独立动画(`ElementCompositionPreview` + `CreateVector3KeyFrameAnimation`) | ❌ 组合动画在应用进程内,**且每帧重画** |
| `BackdropBlurBrush`(元素级 backdrop 模糊) | ❌ 没有对等物(DWM 的系统 backdrop 只能作用于**整窗背后**) |
| `SynchronizedAcrylicBrush` | ⚠ 只有整窗 system backdrop,窗口**内部**内容不行 |
| `DropShadowPanel` | ❌ `BoxShadow` 每层多一次 render pass |
| **固定画布 + `Viewbox` 缩放** | ✅ **可以抄** |
| **逐行 `Opacity` 代替渐变 `OpacityMask`** | ✅ **可以抄** |
| 动态背景默认关 + 设置开关 | ✅ 已有(`MotionEnabled` 宿主门控) |

⇒ 可抄的那三条里,**"固定画布 + Viewbox"是最值钱的**:它把色团层的栅格化面积从
"窗口尺寸"降到"固定尺寸",而且如果同时**去掉漂移里的 `Scale`、只留 `Translation`**,
这一层就有机会变成**可缓存的合成层** ⇒ 每帧只剩"一个带纹理的矩形"。
这正好解释了 `CacheMode="BitmapCache"` / 预渲染位图为什么都白搭(见 `perf-notes.md`)。

## 四、陷阱

- **"默认 Collapsed" ≠ 原版也在跑**。先确认开关状态再比读数。
- **XAML 里的 `Activate_NNN_XXX`、`BusyIndicatorAnimationAdapter` 等符号来自 Telerik 图表库**,
  二进制 grep 时别当成原版自己的证据。
- **XAML 看不出动画跑在哪条线程**,`EnableDependentAnimation="True"` 是 XAML 里唯一能直接看到的信号;
  其余要查 DLL 符号。
