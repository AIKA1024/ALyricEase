# ALyricEase

**ALyricEase** 是 [LyricEase](https://www.microsoft.com/store/productId/9N1MKDF0F4GT) 的 Avalonia 跨平台复刻版：以原版 UWP 应用的交互与视觉为参照，用 **Avalonia 12 + .NET 10（C#）** 从零重写，运行于 **Windows 与 Android**。

> LyricEase 是 Windows 上一款广受好评的第三方网易云音乐客户端（UWP / Fluent Design）。本项目是个人学习性质的复刻实践，并非官方移植，也与原版作者无关。

## 特性

- **双音源**：网易云音乐 / QQ 音乐
- **账号登录**：网易云账号登录、QQ 音乐扫码登录
- **内容浏览**：歌单、搜索（歌曲/歌单）、用户主页、播放队列
- **歌词**：逐行渲染，逐行模糊过渡动画；手动浏览歌词时自动暂停跟随，拉回后渐显恢复
- **封面加载**：异步加载 + 内存 / 磁盘两级缓存，可在设置中统一清理
- **性能**：设置中提供「性能与体验」档位；内置 Headless 探针体系做性能与内存回归

## 平台与构建

| 平台 | 入口项目 | 说明 |
|---|---|---|
| Windows | `src/ALyricEase.Windows` | 支持发布态 NativeAOT 裁剪 |
| Android | `src/ALyricEase.Android` | Android 6.0+（API 23） |

前置要求：[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)。

```bash
# Windows 桌面端
dotnet run --project src/ALyricEase.Windows

# Android（需要先安装 android workload 与 JDK 17+）
dotnet publish src/ALyricEase.Android -f net10.0-android -c Release
```

核心 UI 与业务逻辑集中在 `src/ALyricEase`，各平台入口项目只做宿主适配。

## 开发文档

`docs/` 下沉淀了开发过程中的实测结论与踩坑记录（Avalonia 布局/性能/内存、网易云与 QQ 音乐接口、Android 平台注意事项等），入口见 [docs/README.md](docs/README.md)。

## 免责声明

- 本项目**仅供学习与交流**，请勿用于商业用途；
- 本项目与原版 LyricEase 及其作者、网易云音乐、QQ 音乐官方均**无任何关联**；
- 项目使用非官方接口实现，功能可能随官方调整而失效，使用第三方账号登录产生的一切风险由使用者自行承担；
- 如有内容侵犯您的权益，请联系仓库所有者删除。

## 许可证

本项目以 [GPL-3.0](LICENSE) 协议开源。所依赖的第三方组件与参考实现见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

## 致谢

- **LyricEase**（UWP）—— 原版应用，本项目复刻的参照
- [L-1124/QQMusicApi](https://github.com/L-1124/QQMusicApi) —— QQ 音乐 API 协议参考
- [yakult-green-tea/qq-music-api](https://github.com/yakult-green-tea/qq-music-api) —— QQ 音乐扫码登录协议改编来源
- [AvaloniaUtils/AsyncImageLoader.Avalonia](https://github.com/AvaloniaUtils/AsyncImageLoader.Avalonia) —— 异步图片加载
- [AvaloniaUI](https://github.com/AvaloniaUI/Avalonia) —— 跨平台 UI 框架
