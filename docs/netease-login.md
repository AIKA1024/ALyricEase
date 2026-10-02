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

## 4. 版权/可播性判定:privilege 字段语义(2026-10-02 实测)

网页端判断"歌曲能不能播"完全靠每首歌的 **privilege 对象**(歌单/专辑详情、song/detail 内嵌,
或账号相关地打 `POST /api/song/enhance/privilege`,body `ids=[...]`,带 MUSIC_U 即按账号算)。

核心字段与实测语义:

| 字段 | 含义 | 实测 |
|---|---|---|
| `st` | 版权状态。**<0 = 真无版权(灰色)**,典型 -200 | 周杰伦《依然范特西》(album 18893)匿名与 VIP 完全一致:-200/pl=0,连 VIP 也解不开;差异仅剩 freeTrialPrivilege/ignoreCache 装饰字段 |
| `pl` | 当前账号可播的最高码率(bps)。**pl>0 即可播**,0=不可播 | VIP 账号对 fee=1 歌 pl=999000;匿名 0 |
| `plLevel` | pl 的可读形式(standard/exhigh/lossless/hires/none),`none`=不可播 | 与 pl 一一对应 |
| `fee` | 计费类型:0=免费(但也可能无版权),1=VIP,4=需购专辑,8=低音质免费 | fee=8 匿名可播但 pl 封顶 320000(exhigh);fee=1 匿名 pl=0、VIP pl=999000 |
| `dl`/`dlLevel` | 下载权限,同理 | VIP dl=999000;匿名 fee=1 时 none |
| `maxbr`/`playMaxbr` | **曲库侧**最高码率,不是权限!无版权歌也标 999000/hires | 别拿 maxbr 判断可播性 |

判定规则(网页端行为):**可播 ⇔ `pl > 0`(等价 `plLevel != "none")`**;灰不灰只看 `st < 0`。
fee 只决定"为什么不可播"的提示文案(VIP 锁/需购买),不参与可播判断。

三个坑(实测):

1. **同一概念多接口口径不一**:`GET /api/song/detail`(老明文)内嵌的 privilege 是**静态的**,
   带 MUSIC_U 与匿名返回逐字节相同;账号相关的权益必须打 `POST /api/song/enhance/privilege`。
   匿名 `GET /api/v6/playlist/detail` 顶层 `privileges` 全 null(要登录态才有值);
   `GET /api/v1/album/{id}` 内嵌的 privilege 反而是完整可用的。
2. cookie 要走标准 `Cookie: MUSIC_U=<hex>` 头;实测明文 /api 通道带有效 MUSIC_U 即按登录账号算权益,
   不需要 weapi 加密(vipType=110 账号验证通过)。
3. `noCopyrightRcmd`(song/detail 顶层)= "无版权下架"推荐替代标记,与 st<0 通常同时出现,可作旁证。

对 ALyricEase 的意义:歌单/详情页想提前灰掉不可播的歌(像网页版那样),
用 privilege 的 pl>0 一步判定即可,不必逐首试探 play-url;
需要区分"灰色"与"VIP 锁"时看 st<0 vs fee=1。
