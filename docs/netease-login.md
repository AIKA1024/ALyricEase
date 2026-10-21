# 网易云 / QQ 音乐登录细节

> 从 `.workbuddy/memory/MEMORY.md` 下沉(该文件有注入上限,细节放这里)。
> 相关回归线:扫码/手机验证码 `--nelogin`;登录弹窗布局 `--logindlg`;代理登录 `--neproxy`;
> 账号态/离线歌单导航 `--accountstates`;红心缓存 `--accountlike`。

## 1. 网易云登录方式

1. **手机扫码登录（默认,推荐）**:`NetEaseApiClient.Login.cs` 走 eapi
   `POST /eapi/login/qrcode/unikey` 取 key,本地用 `Net.Codecrete.QrCodeGenerator` 渲染
   `https://music.163.com/login?codekey=<key>` 二维码;轮询 `client/login`
   (800 过期 / 801 等待 / 802 已扫码 / 803 授权)。803 从响应 `Set-Cookie` 提取 `MUSIC_U`,
   再经 `CompleteLoginAsync` 验证资料,失败回滚。
2. **手机号验证码登录**:eapi `POST /eapi/sms/captcha/sent`(带 `secrete`)发送,
   `POST /eapi/w/login/cellphone` 登录,同样以 `Set-Cookie` 的 `MUSIC_U` 为准。
   实测本机网络 weapi 登录端点被风控成空 body,eapi(interface 网关)正常,故两条链路全走 eapi。
3. **MUSIC_U 粘贴**:从浏览器/客户端 Cookie 里复制凭证,手动填(后备)。
4. **官方客户端代理登录**(`NetEaseProxyLoginService`,NuGet `ProjectCirrus.Copycat`):
   起一个本地代理,让**官方 PC 客户端**把登录请求经它转发,从中截获凭证。

⚠ **代理登录仅 Windows**:`ProjectCirrus.Copycat` 的 native 部分只有 `win-x64` / `win-arm64`。
二维码渲染包为纯托管实现,Android/AOT 均可用。

### 登录风控码(2026-10)

网易会按 IP/设备判定登录风险,扫码与手机验证码都可能拿到非 800/801/802/803 的返回:

| code | message | 处理 |
|---|---|---|
| `-462` | 请完成验证操作 | `verifyUrl` 指向 `st.music.163.com/encrypt-pages`;换网络或稍后重试 |
| `8821` | 请切换其他登录方式或升级新版本再试 | 旧版客户端常触发,`type=3` 新接口也会遇到;社区实测为 IP 级风控 |
| `-460` / `8810` | 网络环境存在风险 | 换网络(手机热点)后重试 |

这些码换接口版本绕不开,弹窗会显示「失败(码):服务端消息 + 换网络/MUSIC_U 粘贴」指引;
日志(见 `logging.md`)同时记录 `Code` 便于排查。兜底始终是 `MUSIC_U` 粘贴登录。

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

## 5. QQ 原生登录凭据保存与续签（2026-10-16）

扫码与手机号验证码登录共用 `QQMusicApiClient.ApplyNativeCredential` 保存路径。
`QQMusicQrLoginService` 在资料校验后继续传递 **Login 原始响应**里的
`openid`、`access_token`、`refresh_token`、`refresh_key`、`unionid`、`encryptUin`，
以及 `expired_at`、`musickeyCreateTime`、`keyExpiresIn`；数字与字符串时间值都支持。
不能只保存 musicid/musickey，也不能拿 GetLoginUserInfo 的资料响应替代原始凭据。

以上字段保存在已有 `CookieStore.QQCookieRaw` 中：刷新字段沿用 `psrf_*` 键，
musickey 寿命使用项目元数据键 `alyric_qq_key_expires_in`（秒），加密账号标识使用 `euin`。
凭据刷新成功后也更新创建时间和寿命；重启读取凭据快照时恢复这些字段。
服务端未返回的可选信息不伪造、不串用上一账号的值；原生 MQTT token 兜底及旧 Cookie 仍兼容。
旧版保存时已丢失的字段无法事后恢复，需重新扫码或验证码登录才能保存新响应中的信息。

### 原生续签

- 验证码凭据实测有 `openid` / `access_token` / `refresh_key`，没有 `refresh_token`。
  原生通道以 `refresh_key` 为刷新前提；其余可选字段缺失时发送空值，不能沿用网页 OAuth 的强制门槛。
- 扫码与验证码通过 `alyric_native_login=qr/phone` 标记来源；兼容旧扫码 `tmeLoginType=6`。
  两者都经 `QQMusicQrLoginService.RefreshStoredCredentialAsync`，复用持久设备、QIMEI 与 Android 会话，
  调用 `music.login.LoginServer/Login`（`loginMode=2`，保留各自 `tmeLoginType`）。
  网页 Cookie 保持原 Web comm/OAuth 路径，不能只凭 `loginType=2` 就认为是原生登录。
- 原生凭据在到期前最多 6 小时进入提前刷新窗口（短寿命最多提前四分之一），
  由启动恢复或正常 QQ 业务请求的 `EnsureCredentialValidAsync` 按需触发；不创建后台定时器，应用关闭时不运行。
  刷新后保存新 key、刷新字段、创建时间和寿命，并验证新 key；新 key 缺少时间字段时不借用旧 key 的有效期。
- 并发刷新共用 `_credentialGate`。提前刷新失败时，刚通过服务端校验的旧 key 继续使用，至少等 5 分钟再试。
  已失效凭据的失败重试仍限制为一次；原生成功换得新 key 后可开始新的续签周期。
  续签期间登出/换号的响应不能写回当前账号。

协议参考（只参考行为，C# 实现）：
[L-1124 刷新参数](https://github.com/L-1124/QQMusicApi/blob/HEAD/qqmusic_api/modules/login.py#L121)、
[原生设备通道](https://github.com/yakult-green-tea/qq-music-api/blob/HEAD/src/services/auth/authenticatedReads.ts#L133)、
[提前刷新及重试](https://github.com/yakult-green-tea/qq-music-api/blob/HEAD/src/services/auth/sessionState.ts#L243)。

回归线 `--qq-native-credential`（模拟响应、保存、重启恢复、刷新轮换、缺字段及旧格式兼容），不触网、不打印凭据值。
续签回归线 `--qq-native-refresh`（无 refresh_token、Android/Web 通道区分、提前刷新、失败退避、轮换、并发、换号和取消）。
续签的触发、结果和重试计划通过现有 Serilog 文件日志记录，字段与排查入口见 [logging.md](logging.md)。
