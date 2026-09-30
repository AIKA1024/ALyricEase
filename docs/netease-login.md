# 网易云 / QQ 音乐登录细节

> 从 `.workbuddy/memory/MEMORY.md` 下沉(该文件有注入上限,细节放这里)。
> 相关回归线:代理登录 `--neproxy`;账号态/离线歌单导航 `--accountstates`;红心缓存 `--accountlike`。

## 1. 两种登录方式

1. **MUSIC_U 粘贴**:从浏览器/客户端 Cookie 里复制凭证,手动填。
2. **官方客户端代理登录**(`NetEaseProxyLoginService`,NuGet `ProjectCirrus.Copycat`):
   起一个本地代理,让**官方 PC 客户端**把登录请求经它转发,从中截获凭证。

⚠ **仅 Windows**:`ProjectCirrus.Copycat` 的 native 部分只有 `win-x64` / `win-arm64`。

### 为什么能 MITM

网易云 PC 客户端**不校验 TLS 证书**,所以本地代理能做中间人拿到明文请求。
代价是:登录成功后客户端里的代理设置还指着这个代理(**代理进程已退出**),
用户之后在客户端里联网会失败。⇒ **登录成功后必须提醒用户关闭客户端里的代理设置。**

## 2. eapi 加密:项目变体 ≠ 标准客户端格式(别混)

两套东西长得像,但 header 的编码方式不同,混用会得到"服务端 400 / 空响应"这类不指向根因的错误。

| | 项目内 `CryptoService.EncryptEapi` | 标准客户端 |
|---|---|---|
| 明文布局 | `"nobody{use}{this}" + text` | `path-36cd479b6b5-text-36cd479b6b5-md5` |
| header 形态 | **对象** | **字符串化 JSON**(即 JSON 又 encode 成字符串再进三段式) |
| 分隔符 | — | `36cd479b6b5`(固定魔数) |

标准客户端的三段式:第一段是请求 path,第二段是 JSON 序列化后的参数/header 字符串,
第三段是 `md5`。项目里对接自己构造的请求时用的是私有变体 —— 需要对照标准格式时,
不要拿 `EncryptEapi` 的输出直接去比对。

回归线 `--neproxy` 覆盖代理登录这条链路。

## 3. QQ 音乐 ag-1 写通道:Add 必须带 Android 客户端身份 comm(2026-09-30)

红心收藏(PlaylistDetailWrite/**AddSonglist**)与向自有歌单加歌必须用 **Android comm**
(ct=11/cv=14090008 + authst=musickey + tmeLoginType/qq,见 QQMusicApiClient.WriteAndroidClientComm);
web comm(cv=4747474/uin+g_tk)打 Add 一律被拒 —— 服务端回 80105 或 500026(随轮次波动),
而 **80105 会被 UI 误判成登录过期弹重登**(ShouldPromptRelogin)。
DelSonglist 对两种 comm 都放行;PlaylistBaseWrite/AddPlaylist(创建歌单)也不受限。
参数形状(dirId/tid/bFmtUtf8、songMid 有无、布尔 vs 整型)实测全部无关 —— 别往参数方向排查。
坑:写操作落库有秒级延迟,Add 后立刻 Del 回 2001(暂不存在),不是错误,延迟重试即可。
回归线 --qqadd(分变体诊断 + 生产路径端到端 + 终态校验);参考实现 L-1124/QQMusicApi(GPL-3.0)。
