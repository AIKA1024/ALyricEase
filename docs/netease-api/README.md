# 网易云音乐 API 逆向文档（ALyricEase 实践版）

> 本文档是 ALyricEase 项目在逆向网易云音乐 Web API 过程中的**实测记录与汇总**，
> 不是第三方资料翻译。所有结论均来自 `NetEaseApiClient` / `CryptoService` 的真实代码路径
> 与本地抓包验证。适用日期：**2026-08**。
>
> 相关源文件（一切以代码为准）：
> - `ALyricEase/Services/NetEase/NetEaseApiClient.cs` — 端点全集 + 双通道回落
> - `ALyricEase/Services/Crypto/CryptoService.cs` — weapi/eapi 加密
> - `ALyricEase/Services/Crypto/RsaPadding.cs` — 自定义 RSA
> - `ALyricEase/Services/NetEase/CnIpPool.cs` — CN IP 池
> - `ALyricEase/Models/Dtos/{Weapi,Legacy,Eapi}Dtos.cs` + `NetEaseJsonContext.cs` — 响应结构

---

## 0. 一句话总结

网易云 Web 接口分两类通道：

| 通道 | 形态 | 用途 | 本项目状态 |
|---|---|---|---|
| **weapi / eapi** | 加密 POST（表单 `params` [+`encSecKey`]） | 官方客户端主通道 | 本机被 WAF 拦截，已自动回落 |
| **明文 GET `/api/...`** | 无加密、带 cookie 即可 | 网页版同源接口 | **本项目主通道**，全可用 |

**双通道架构**（本项目核心设计）：每个功能先走加密 POST，收到空 body（风控特征码 `-3`）即置 `_wafBlocked` 位，后续请求全部改走明文 GET。同一功能的加密/明文两种路径字段名不同（详见端点文档），需要两套 DTO。

---

## 1. 为什么会有双通道（风控背景）

2026-08 实测：从本机（广东珠海 IP，`music.163.com` 可达）发出**任意 weapi/eapi 加密 POST**
——包括匿名注册、搜索、歌词、播放地址——都返回 **HTTP 200 + 空 body**，与加密是否字节级正确无关
（已验证加密正确，Python requests 同样被拦），也非 cookie 前提（暖场 GET 不产生 cookie）。
疑似**地区级 WAF 针对加密通道**。

**明文 GET `/api/...` 在同一网络下可用**，且大多**不需要登录 cookie**。

> 境内机器（无该 WAF 拦截）加密通道可能直接可用，不触发回落。回落逻辑对最终用户透明。

---

## 2. 认证体系

网易云 Web API 的认证 = **cookie**，没有 HTTP 头/令牌。

| Cookie | 含义 | 有效期 | 获得方式 |
|---|---|---|---|
| `MUSIC_A` | 匿名用户会话 | 约 14 天（本项目按 14 天缓存） | `POST /weapi/register/anonimous`（加密） |
| `MUSIC_U` | 登录用户会话 | 长期（重新登录前一直有效） | 从浏览器 `music.163.com` 登录态拷贝 |
| `__csrf` | CSRF 令牌 | 随会话 | 登录页加载时下发，多数 GET 不需要 |

- 匿名 cookie `MUSIC_A` 用于：搜索、歌词、匿名播放地址、公开歌单、首页推荐。
- 登录 cookie `MUSIC_U` 额外解锁：用户资料、我的歌单、每日推荐、红心喜欢、VIP 音质。
- **明文 GET 接口大多不校验登录**（返回匿名可用的数据或 `code 301`/空），登录态只影响权限类数据。

### 获取 MUSIC_U（浏览器）

1. Chrome / Edge 打开 `https://music.163.com`，登录。
2. F12 → Application → Cookies → `https://music.163.com` → 复制 `MUSIC_U` 的值（一串 hex）。
3. 可容忍整段 `Cookie:` 字符串，本项目 `SetMusicUCookie` 会自动截取 `MUSIC_U=` 之后到 `;` 的部分。

> ⚠️ `MUSIC_U` 等同于登录凭证，**不要提交到任何仓库/文档**。本项目存于
> `%LocalAppData%\ALyricEase\config\cookie.json`（明文），日志输出一律打码（`MUSIC_U=ab***cd`）。

---

## 3. 请求头基线

所有请求统一（`ApplyCommonHeaders`）：

```
User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36
Referer: https://music.163.com
Cookie: MUSIC_U=...; MUSIC_A=...   // 视端点
X-Real-IP: 114.114.114.114         // 仅播放地址端点，随机 CN IP
```

`X-Real-IP` 只加在播放地址请求上：网易按 IP 做地理围栏，海外 IP 直接返回 `url:null`。
CN IP 池见 `CnIpPool.cs`（22 个大陆公共 DNS / 云厂商 IP，随机取）。

---

## 3.5 信息来源：网页 JS 是最权威参照（实测）

除了实测抓包，**直接分析 `music.163.com` 的网页 JS** 是获取 API 信息的最权威途径——
它就是官方客户端代码本身，永不过时。2026-08 实测结论：

1. **抓核心 JS**（首页 HTML → `core_<hash>.js`，约 700KB）：
   ```
   curl -s https://music.163.com/ | grep -oE 'src="[^"]*\.js[^"]*"'
   # 核心包形如 //s3.music.126.net/web/s/core_<hash>.js?<hash>
   ```
2. **提取全部端点**（一次性拿到真实路径清单）：
   ```
   grep -oE '"/?(weapi|eapi|api)/[a-zA-Z0-9/_.-]+"' core.js | tr -d '"' | sort -u
   ```
   实测命中本项目用的全部端点：`/api/v6/playlist/detail`、`/api/song/lyric`、
   `/api/user/playlist`、`/api/song/enhance/player/url/v1`、`/api/cloudsearch/get/web` 等。
   （搜索明文回落用的 `/api/cloudsearch/pc` 是 PC 客户端端点，不在 Web core.js 里。）
3. **加密入口在 JS 里叫 `window.asrsea`**，四个参数是**词表动态解码**的：
   ```js
   var bVk4q = window.asrsea(JSON.stringify(payload),
       bxo6m(["流泪","强"]),      // 词表查值拼成 0x010001 = RSA 指数 E
       bxo6m(BF2e.md),           // 密钥模块(动态下载的 nm.x.ek)
       bxo6m(["爱心","女孩","惊恐","大笑"])); // IV 相关
   ```
   所以**加密常量不能直接在 JS 里搜明文**（`0CoJUm6Qyw8W8jud` 等搜索不到），
   它们藏在中文词表 `BF2e.emj` 里，按字查 hex 再拼接。这解释了为什么
   抓 JS 验证常量要"按词表拼"而非"按字符串找"。
4. **Web API 与 eapi 是同一套路径**：JS 里是 `/api/...`，提交时 `X0a.replace("api","weapi")`
   或打 `/eapi/...`——印证了加密/明文双通道共用路径的架构。

> 取用建议：**端点清单以网页 JS 为准**（最新），**加密算法以本项目 `01-crypto.md` 为准**
> （算法稳定，社区多年验证）。两者交叉验证最可靠。

---

## 4. 目录

| 文档 | 内容 |
|---|---|
| [01-crypto.md](01-crypto.md) | weapi / eapi 加密算法逐字节拆解（常量、步骤、输出格式） |
| [02-endpoints.md](02-endpoints.md) | 全部端点：路径、方法、参数、响应样例、字段差异 |
| [03-antiban.md](03-antiban.md) | 反封号：请求节制、降级链、批量上限、频率建议 |
| [04-verify-selftest.md](04-verify-selftest.md) | 端到端验证清单（`--selftest` 覆盖了哪些） |
