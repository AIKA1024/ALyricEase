using System.Net;
using System.Text;
using ALyricEase.Services.Auth;
using ALyricEase.Services.NetEase;
using ALyricEase.Services.QQMusic;

namespace ALyricEase.Headless;

/// <summary>原生凭证校验响应回归：模拟 HTTP，不访问真实账号或网络。</summary>
internal static class QqLoginResponseProbe
{
    public static async Task<int> RunAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"aly-qq-response-{Guid.NewGuid():N}");
        var cookie = new CookieStore(Path.Combine(dir, "cookie.json"));
        (string Name, string Json, bool? Valid, int? Error)[] cases =
        [
            ("valid nested info", """{"code":0,"req_0":{"code":0,"data":{"info":{"nick":"probe"}}}}""", true, null),
            ("valid empty metadata", """{"code":0,"req_0":{"code":0,"data":{}}}""", true, null),
            ("expired without data", """{"code":0,"req_0":{"code":104401}}""", false, null),
            ("expired with null data", """{"code":0,"req_0":{"code":104400,"data":null}}""", false, null),
            ("expired code 1000", """{"code":0,"req_0":{"code":1000}}""", false, null),
            ("global expiry without item", """{"code":104401}""", false, null),
            ("global expiry without data", """{"code":104400,"req_0":{"code":0}}""", false, null),
            ("business error without data", """{"code":0,"req_0":{"code":20279}}""", null, 20279),
            ("item error takes precedence", """{"code":1000,"req_0":{"code":20279}}""", null, 20279),
            ("success without data", """{"code":0,"req_0":{"code":0}}""", null, -2),
            ("success with null data", """{"code":0,"req_0":{"code":0,"data":null}}""", null, -2),
            ("success with array data", """{"code":0,"req_0":{"code":0,"data":[]}}""", null, -2),
            ("success without item", """{"code":0}""", null, -2),
            ("null item", """{"code":0,"req_0":null}""", null, -2),
            ("missing status", """{"req_0":{"data":{}}}""", null, -2),
            ("non-object root", "[]", null, -2),
            ("invalid JSON", "<html>upstream error</html>", null, -2),
        ];
        var failures = 0;
        try
        {
            foreach (var loginType in new[] { 6, 2 })
            foreach (var test in cases)
            {
                using var service = new QQMusicQrLoginService(cookie, new HttpClient(new ResponseHandler(test.Json)));
                try
                {
                    var valid = await service.ValidateStoredCredentialAsync("123456789", "probe-key", loginType,
                        CancellationToken.None);
                    if (test.Error is not null || valid != test.Valid)
                        throw new InvalidOperationException($"Expected valid={test.Valid}, error={test.Error}; got valid={valid}");
                }
                catch (ApiException ex) when (ex.Code == test.Error)
                {
                }
                catch (Exception ex)
                {
                    failures++;
                    Console.Error.WriteLine($"[qq-login-response] FAIL {loginType}/{test.Name}: {ex}");
                }
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
        Console.WriteLine($"[qq-login-response] {(failures == 0 ? "PASS" : "FAIL")} {cases.Length * 2} cases, {failures} failures");
        return failures == 0 ? 0 : 1;
    }

    private sealed class ResponseHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
    }
}
