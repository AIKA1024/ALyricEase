using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ALyricEase.Services;
using ALyricEase.Services.Auth;
using ALyricEase.Services.NetEase;
using ALyricEase.Services.QQMusic;

namespace ALyricEase.Headless;

/// <summary>QQ 关注歌手写接口诊断探针(--qqconcern)：复现"点关注就弹登录窗"。
/// 取一个**自己已经关注**的歌手，用关注(opertype=0)重发一次 —— 幂等，不改动关注状态 ——
/// 分别打四条通道并打印真实返回码：
/// A 明文 musicu / 新模块、B 明文 musicu / 老模块、C ag-1 加密 / web comm、D ag-1 加密 / Android comm。
/// 目的是区分"写通道不认这套身份(80105)"与"参数/模块不对(其他码)"。</summary>
public static class QqConcernDiagProbe
{
    /// <summary>兑底用歌手 mid（关注列表为空时才用；号码来自公开曲库样例）。</summary>
    private const string FallbackSingerMid = "0025NhlN2yWrP4";

    public static async Task<int> RunAsync(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var cookie = new CookieStore();
        var api = new QQMusicApiClient(cookie);
        Console.WriteLine($"[qqconcern] IsLoggedIn={api.IsLoggedIn}");
        if (!api.IsLoggedIn)
        {
            Console.WriteLine("[qqconcern] 未登录，先登录再跑");
            return 1;
        }

        try
        {
            await api.EnsureCredentialValidAsync();
            Console.WriteLine("[qqconcern] 0) 凭证校验: 通过");
        }
        catch (ApiException ex)
        {
            Console.WriteLine($"[qqconcern] 0) 凭证校验: FAIL code={ex.Code} msg={ex.Message}");
            return 1;
        }

        // 1) 找一个"已经关注"的歌手：对已关注对象再发关注是幂等的
        string mid;
        try
        {
            var page = await api.GetFollowSingersAsync("", 0, 20, default);
            Console.WriteLine($"[qqconcern] 1) 关注歌手 {page.Users.Count} 位 Total={page.Total} " +
                              $"HasMore={page.HasMore} 首条=[{page.Users.FirstOrDefault()?.Nickname}]");
            mid = page.Users.Select(u => u.SingerMid).FirstOrDefault(m => m.Length > 0) ?? FallbackSingerMid;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[qqconcern] 1) 关注列表读取失败 {ex.GetType().Name}: {ex.Message}");
            mid = FallbackSingerMid;
        }
        Console.WriteLine($"[qqconcern] 样例歌手 mid={mid}");

        // 生产路径自查(幂等)：对已关注对象再走一次 SetSingerFollowedAsync，验证 ag-1 + Android comm
        if (args.Contains("prod"))
        {
            try
            {
                await api.SetSingerFollowedAsync(mid, follow: true, default);
                Console.WriteLine("[qqconcern] prod) SetSingerFollowedAsync(已关注→再关注): 成功(幂等，状态不变)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[qqconcern] prod) SetSingerFollowedAsync: FAIL {ex.GetType().Name} " +
                                  $"code={(ex as ApiException)?.Code} {Trim(ex.Message)}");
                return 2;
            }
            return 0;
        }

        // 2) 四条通道各打一次"再关注一次"
        await Probe("A 明文 musicu / 新模块", () => api.ProbeSingerConcernAsync(
            mid, true, true, secureChannel: false, androidComm: false, null, default));
        await Probe("B 明文 musicu / 老模块", () => api.ProbeSingerConcernAsync(
            mid, true, false, secureChannel: false, androidComm: false, null, default));
        await Probe("C ag-1 加密 / web comm", () => api.ProbeSingerConcernAsync(
            mid, true, true, secureChannel: true, androidComm: false, null, default));
        await Probe("D ag-1 加密 / Android comm", () => api.ProbeSingerConcernAsync(
            mid, true, true, secureChannel: true, androidComm: true, null, default));

        // 3) 参数对照：opertype=9(非法值) 走明文与加密各一次，看是否只回"参数错误"
        await Probe("E 明文 / opertype=9", () => api.ProbeSingerConcernAsync(
            mid, true, true, secureChannel: false, androidComm: false, 9, default));
        await Probe("F ag-1+Android / opertype=9", () => api.ProbeSingerConcernAsync(
            mid, true, true, secureChannel: true, androidComm: true, 9, default));

        Console.WriteLine("[qqconcern] 判定: 任一通道 code=0 ⇒ 该通道可用；全为 80105 ⇒ 身份/传输层问题；" +
                          "全为 1000 ⇒ 需重新登录。");
        return 0;
    }

    private static async Task Probe(string label, Func<Task<JsonDocument>> action)
    {
        try
        {
            using var doc = await action();
            var root = doc.RootElement;
            var top = Pick(root, "code");
            var reqCode = root.TryGetProperty("req_0", out var r0) ? Pick(r0, "code") : "n/a";
            var ret = root.TryGetProperty("req_0", out var r1) && r1.TryGetProperty("data", out var d)
                ? $"retCode={Pick(d, "retCode")}/{Pick(d, "RetCode")} msg={Str(d, "code_msg") ?? Str(d, "msg")}"
                : "(无 data)";
            Console.WriteLine($"[qqconcern] {label}: top={top} req_0.code={reqCode} {ret}");
        }
        catch (ApiException ex)
        {
            Console.WriteLine($"[qqconcern] {label}: ApiException code={ex.Code} {Trim(ex.Message)}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[qqconcern] {label}: {ex.GetType().Name} {Trim(ex.Message)}");
        }
    }

    private static string Pick(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) &&
           v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n.ToString() : "-";

    private static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) &&
           v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Trim(string message)
        => message.Length <= 260 ? message : message[..260] + "…";
}
