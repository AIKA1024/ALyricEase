# Windows 系统分享面板(`MenuFlyout` → 分享)

> 用户报的症状只有一句:「点分享没反应」。生产路径把异常吞成 `false`,所以每次都得靠日志 +
> `--sharetest` 探针定位。**这条链路有两个独立的静默失败条件,踩过两次,各记一条。**

## 一、链路

| 步骤 | 代码 |
|---|---|
| 菜单项 | `SongContextMenu.ShareSongAsync`(`TrackRow` 行菜单) / `PlaylistView.OnSharePlaylistClick`(歌单 more 菜单) |
| 服务 | `IPlatformShareService.ShareUriAsync(ownerHandle, title, description, uri)` |
| 平台实现 | `src/ALyricEase.Windows/Services/WindowsShareService.cs` |
| interop | `src/ALyricEase.Windows/Services/ShareInterop.cs`(RoGetActivationFactory + vtable) |
| 诊断 | 环境变量 `ALY_SHARE_DIAG=1` ⇒ `%TEMP%\aly-share-diag.log`(Debug 构建默认开) |

`DataTransferManager` 的静态入口只适用于 UWP;Win32/Avalonia 窗口必须用
`IDataTransferManagerInterop` 传 HWND,并且要自己接 `DataRequested` 填数据。

## 二、失败条件 1:`ShowShareUIForWindow` 要求 owner 是**前台**窗口(2026-10-02)

不在前台时它**不抛异常、不弹面板**(返回值也看不出),唯一的可靠观测量是 `DataRequested`
有没有触发。歌曲菜单是 Avalonia Popup(独立 HWND),点击菜单项时前台可能是弹窗而不是主窗口
⇒ `ShareUriAsync` 里先 `SetForegroundWindow(ownerHandle)` 再调。

- 回归:`<publish>\ALyricEase.Windows.exe --sharetest click`(SendInput 真实点击菜单项,走生产路径)
- 判据:`[share] DataRequested 触发` = 面板真开了;`DataRequested=False` = 调用没抛但面板没出来。

## 三、失败条件 2:NativeAOT 没有内置 COM,`As<T>()` 在发布版直接抛(2026-10-25)

旧写法(`DataTransferManager.As<IDataTransferManagerInterop>()`,MS 的 Win32 分享示例就是它)
依赖**内置 COM 互操作**:`[ComImport]` 接口 + `Marshal.GetComInterfaceForObject` 反射建桩。
**NativeAOT 默认不编内置 COM**,发布版里这行抛:

```
NotSupportedException: COM Interop requires ComWrapper instance registered for marshalling.
(0x80131515)
```

它原本写在 `WindowsShareService` 的**字段初始化**上 ⇒ DI 解析单例时抛 ⇒ 调用方的 catch 吞掉
⇒ 用户看到的就是"点分享没反应"。JIT(Debug/Release 非 AOT)一切正常,所以**只在 Debug 上跑
探针永远复现不出来** —— 这是第二次踩坑(第一次修完在前台条件下验过了,发布版依旧不弹)。

修法:不碰内置 COM。`ShareInterop` 用 `RoGetActivationFactory` 拿激活工厂,按 vtable 槽位调
`IDataTransferManagerInterop`(IUnknown 派生:3=`GetForWindow`,4=`ShowShareUIForWindow`),
返回的 ABI 指针仍交给 CsWinRT 的 `MarshalInterface<DataTransferManager>.FromAbi` 包成托管对象,
事件订阅与数据填充走正常投影。同项目的 `WindowsAudioSessionMeter`(WASAPI)用的是同一套路。

- 复现/回归:**必须是 AOT 发布产物**
  `dotnet publish src/ALyricEase.Windows/ALyricEase.Windows.csproj -c Release -r win-x64 --self-contained true -p:PublishAot=true -o artifacts/aot-check`
  然后 `artifacts/aot-check/ALyricEase.Windows.exe --sharetest click`
- 探针会先打印两条路径的成败(`内置 COM 可用/不可用`、`旧路径 As<T>()`、`生产路径 ShareInterop`),
  AOT 上期望:**内置 COM 不可用 + 旧路径抛 NotSupportedException + ShareInterop 可用 + DataRequested=True**。

## 四、结论清单

1. 分享的 interop 只能走 `ShareInterop`,别写回 `As<T>()` / `[ComImport]`。
2. `--sharetest` 有 JIT 与 AOT 两种宿主,`DataRequested` 只在**发布版**上才算验过。
3. 以后凡是"JIT 正常、装了包就不行"且链路里有 Win32/WinRT 互操作,先怀疑内置 COM(AOT 默认关)。
