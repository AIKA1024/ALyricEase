using ALyricEase.Services;
using ALyricEase.Services.Auth;
using ALyricEase.Services.Crypto;
using ALyricEase.Services.NetEase;

namespace ALyricEase.Headless;

/// <summary>网易云扫码 / 手机验证码登录探针(--nelogin):
/// 走真实 eapi 生成二维码并轮询一次(不扫码),再用非法手机号确认短信端点可达。
/// 只读取接口响应,不写登录存档。</summary>
public static class NetEaseLoginProbe
{
    public static async Task<int> RunAsync()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        // 探针用独立 cookie 文件:匿名注册会落盘,不能覆盖用户真实登录存档。
        var probeStore = new CookieStore(Path.Combine(Path.GetTempPath(), "aly-nelogin-probe-cookie.json"));
        var api = new NetEaseApiClient(new CryptoService(), new CnIpPool(), probeStore);
        try
        {
            var session = await api.CreateQrLoginAsync();
            Console.WriteLine($"[nelogin] 匿名 MUSIC_A={(probeStore.AnonymousMusicA is { Length: > 0 } a ? $"已获取({a.Length}B)" : "缺失")}");
            var pngOk = session.QrPng.Length > 8 &&
                        session.QrPng[0] == 0x89 && session.QrPng[1] == 0x50 &&
                        session.QrPng[2] == 0x4E && session.QrPng[3] == 0x47;
            if (pngOk)
            {
                using var stream = new MemoryStream(session.QrPng, writable: false);
                using var bitmap = new Avalonia.Media.Imaging.Bitmap(stream);
                Console.WriteLine($"[nelogin] 二维码解码尺寸={bitmap.PixelSize}");
                pngOk = bitmap.PixelSize.Width >= 150 && bitmap.PixelSize.Height >= 150;
                var qrPath = Path.Combine(Path.GetTempPath(), "aly-netease-qr.png");
                await File.WriteAllBytesAsync(qrPath, session.QrPng);
                Console.WriteLine($"[nelogin] 二维码图片={qrPath}");
            }
            Console.WriteLine($"[nelogin] 二维码 key={session.Key}, png={session.QrPng.Length}B, valid={pngOk}");
            if (string.IsNullOrWhiteSpace(session.Key) || !pngOk)
            {
                Console.WriteLine("[nelogin][FAIL] 二维码 key/PNG 生成失败");
                return 1;
            }

            var status = await api.CheckQrLoginAsync(session.Key);
            Console.WriteLine($"[nelogin] 轮询状态={status.Stage}");
            if (status.Stage != NetEaseQrLoginStage.Waiting)
            {
                Console.WriteLine("[nelogin][FAIL] 新二维码应处于等待扫码状态");
                return 1;
            }

            var expired = await api.CheckQrLoginAsync("00000000-0000-0000-0000-000000000000");
            Console.WriteLine($"[nelogin] 伪造 key 状态={expired.Stage}");
            if (expired.Stage != NetEaseQrLoginStage.Expired)
            {
                Console.WriteLine("[nelogin][FAIL] 无效 key 应返回过期状态");
                return 1;
            }

            try
            {
                // 999 不是有效国家代码:本地格式校验会放行,由服务端拒绝(不会真的发短信)。
                await api.SendPhoneCodeAsync("000000000", "999");
                Console.WriteLine("[nelogin][FAIL] 无效国家代码未被服务端拒绝");
                return 1;
            }
            catch (ApiException ex)
            {
                Console.WriteLine($"[nelogin] 短信端点可达: code={ex.Code}, message={ex.Message}");
            }

            try
            {
                await api.LoginWithPhoneCodeAsync("000000000", "999", "000000");
                Console.WriteLine("[nelogin][FAIL] 无效手机号登录未被服务端拒绝");
                return 1;
            }
            catch (ApiException ex)
            {
                Console.WriteLine($"[nelogin] 手机登录端点可达: code={ex.Code}, message={ex.Message}");
            }

            Console.WriteLine("[nelogin][PASS] eapi 二维码生成/轮询、短信发送与手机登录端点均可达");
            return 0;
        }
        catch (Exception ex)
        {
            if (ex is ApiException ae)
                Console.WriteLine($"[nelogin][FAIL] code={ae.Code} verifyUrl={ae.VerifyUrl ?? "-"} message={ae.Message}");
            else
                Console.WriteLine($"[nelogin][FAIL] 异常: {ex}");
            return 1;
        }
    }
}
