# weapi / eapi 加密算法拆解

> 实现：`ALyricEase/Services/Crypto/CryptoService.cs` + `RsaPadding.cs`。
> 算法与各语言移植实现一致（参考 wwh1004/NeteaseCloudMusicApi 的字节级交叉验证）。

---

## 1. 常量

| 常量 | 值 | 用途 |
|---|---|---|
| `presetKey` | `0CoJUm6Qyw8W8jud`（16 字节） | weapi 第一层 AES-128 |
| `iv` | `0102030405060708` | weapi 两层 CBC 共用 |
| `eapiKey` | `e82ckenh8dichen8`（16 字节） | eapi AES-128 |
| RSA `E` | `0x010001` | weapi 自定义 RSA 公钥指数 |
| RSA `N` | 见下 | weapi 自定义 RSA 模数 |

RSA 模数（1024 位）：

```
00e0b509f6259df8642dbc35662901477df22677ec152b5ff68ace615bb7b725152b
3ab17a876aea8a5aa76d2e417629ec4ee341f56135fccf695280104e0312ecbda925
57c93870114af6c9d05c4f7f0c3685b7a46bee255932575cce10b424d813cfe4875d3
e82047b97ddef52741d546b8e289dc6935b3ece0462db0a22b8
```

---

## 2. weapi（搜索 / 歌词 / 匿名注册 / v3 详情）

### 2.1 加密步骤

```
输入: text = 紧凑 JSON 序列化的 payload（如 {"s":"晴天","type":1,"limit":30,"offset":0,"csrf_token":""}）
      注意: payload 必须"紧凑无空白 + 字典项恒写"，与官方 Node JSON.stringify 一致。

1. 生成 16 位随机 secretKey:
     secretKey = 16 个随机字符 ∈ [0-9a-zA-Z]
2. 内层加密:
     inner  = base64( AES-128-CBC( text, key=presetKey, iv=iv, pad=PKCS7 ) )
3. 外层加密:
     params = base64( AES-128-CBC( inner, key=secretKey, iv=iv, pad=PKCS7 ) )
4. encSecKey:
     encSecKey = RSA( reverse(secretKey) )    // reverse = 字符串倒序
5. 提交表单: params + encSecKey（见 2.2）
```

> 官方版本 secretKey 固定 16 位 `abcdefghijklmnop`，随机亦可（每请求一 key 更安全）。

### 2.2 自定义 RSA（RsaPadding.cs）

**不是** PKCS#1 v1.5 / OAEP。做法：

```
m = BigInteger( UTF8Bytes(reverse(secretKey)), isUnsigned, isBigEndian )
c = m^E mod N
encSecKey = c.ToString("x2").PadLeft(256, '0')   // 256 位小写 hex（128 字节左补 0）
```

- 明文直接当大整数做模幂，无填充，无格式头。
- 标准 `RSACryptoServiceProvider` 不支持这种裸模幂，**必须手写 `BigInteger.ModPow`**。

### 2.3 HTTP 请求

```
POST https://music.163.com/weapi/register/anonimous?csrf_token=   （以匿名注册为例）
Content-Type: application/x-www-form-urlencoded

params=<base64>&encSecKey=<256位hex>
```

路径规则：`{BaseUrl}/{apiPath}?csrf_token=`，`apiPath` 如 `weapi/search/get/web`。

---

## 3. eapi（播放地址，本项目唯一 eapi 端点）

### 3.1 加密步骤

```
输入: urlPath = 明文路径，如 /api/song/enhance/player/url/v1
      payload = 业务参数，如 {"ids":"[123]","level":"higher"}

1. 构造 eapi body JSON（多一层 method/url 包装）:
     body = {"method":"POST","url":urlPath,"params":{ ...payload }}
2. 拼接消息:
     message = "nobody{use}{this}" + body     // 固定前缀
3. 加密:
     params = hexLower( AES-128-ECB( message, key=eapiKey, pad=PKCS7 ) )
     // ECB，无 IV；输出 16 进制小写字符串，不是 base64
4. 提交表单: 只带 params（无 encSecKey）
```

### 3.2 HTTP 请求

```
POST https://music.163.com/eapi/song/enhance/player/url/v1
Content-Type: application/x-www-form-urlencoded

params=<hex>
```

路径映射：`/api/song/enhance/player/url/v1` → HTTP 请求打到 `/eapi/song/enhance/player/url/v1`。

---

## 4. 与官方实现的字节级差异排查要点

| 检查点 | 常见错误 |
|---|---|
| payload 序列化 | 带空格/换行、省略空字段 → 加密结果全变 |
| weapi 两层顺序 | 必须先 presetKey 内层、再 secretKey 外层 |
| eapi 前缀 | `nobody{use}{this}` 写错（`nobody` 后大括号无空格） |
| eapi 输出 | 应 hex 小写；base64 是错的 |
| RSA | 模幂结果左补 0 到 256 位；BigEndian、isUnsigned |
| 中文 | UTF-8 编码，非 Unicode 转义 |

调试手段：`CryptoService.EncryptWeapiWithKey(jsonText, "abcdefghijklmnop")` 用固定 key，
与 Python/Node 参考实现对拍（见 `SelfTest.RunAsync` 开头）。

---

## 5. 双通道中的加密角色

- 加密 POST 是"主路径"，但本机被 WAF 丢弃（HTTP 200 空 body）→ 内部错误码 `BlockedCode = -3`。
- `PostJsonAsync` 对空 body 抛 `ApiException(BlockedCode)`，各端点方法捕获后置 `_wafBlocked = true`，
  此后同一会话内**不再尝试加密 POST**，全部走明文 GET。
- 明文 GET 不需要任何加密，但字段名与 weapi 版本不同（`artists/album/duration` vs `ar/al/dt`）。
