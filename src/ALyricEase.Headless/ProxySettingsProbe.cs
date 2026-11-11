using ALyricEase.Services;
using ALyricEase.Services.Audio;
using ALyricEase.Services.Network;
using ALyricEase.ViewModels;

namespace ALyricEase.Headless;

/// <summary>应用网络代理设置回归(--proxy):纯逻辑 + 临时 state.json,不碰真实用户配置、不触网。
///
/// 要守住的不变量:
/// ① 三种模式的解析:直连 = GetProxy 返回 null(不是"返回目标地址"),手动 = 固定端点;
/// ② 全局出口是**动态**实例 —— 改设置后不需要重建任何 HttpClient;
/// ③ 手动配置校验:主机为空/端口越界不允许保存,且失败时**不能**覆盖已生效的配置;
/// ④ 粘贴 "http://127.0.0.1:7890/" 能正确剥成主机 + 端口;
/// ⑤ 保存即落盘,重新加载后恢复;
/// ⑥ 旧 state.json(没有 Proxy* 字段)按跟随系统处理,不是直连。</summary>
internal static class ProxySettingsProbe
{
    public static int Run()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var root = Path.Combine(Path.GetTempPath(), $"aly-proxy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var failures = 0;
        try
        {
            failures += ResolveModes();
            failures += RejectInvalidDraft(Path.Combine(root, "invalid"));
            failures += SaveAppliesAndPersists(Path.Combine(root, "save"));
            failures += ReloadRestores(Path.Combine(root, "reload"));
            failures += LegacyStateFallsBackToSystem(Path.Combine(root, "legacy"));
        }
        finally
        {
            NetworkProxy.Apply(ProxySnapshot.SystemDefault);
            try { Directory.Delete(root, true); } catch { }
        }

        Console.WriteLine(failures == 0
            ? "[proxy] PASS 三态解析、动态出口、草稿校验与拒绝、保存落盘、重载恢复、旧配置回落"
            : $"[proxy] FAIL: {failures}");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>三种模式的解析语义。直连必须是 null —— 返回目标地址虽然也算直连,但语义含糊。</summary>
    private static int ResolveModes()
    {
        var failures = 0;
        var target = new Uri("https://music.163.com/api/search");

        NetworkProxy.Apply(new ProxySnapshot(ProxyMode.Direct));
        Expect(ref failures, NetworkProxy.Instance.GetProxy(target) is null, "直连模式的 GetProxy 应为 null");
        Expect(ref failures, NetworkProxy.Instance.IsBypassed(target), "直连模式的 IsBypassed 应为 true");

        NetworkProxy.Apply(new ProxySnapshot(ProxyMode.Manual, "127.0.0.1", 7890));
        Expect(ref failures,
            NetworkProxy.Instance.GetProxy(target) == new Uri("http://127.0.0.1:7890"),
            "手动模式应解析为指定端点");
        Expect(ref failures, !NetworkProxy.Instance.IsBypassed(target), "手动模式不应标记 bypass");

        NetworkProxy.Apply(new ProxySnapshot(ProxyMode.Manual, "127.0.0.1", 7890, "user", "secret"));
        Expect(ref failures,
            NetworkProxy.Instance.Credentials is System.Net.NetworkCredential { UserName: "user" },
            "手动模式填了用户名应带出凭据");

        // 手动但端点不完整:按直连处理(保存前校验过,这里只是兜底,不能抛)
        NetworkProxy.Apply(new ProxySnapshot(ProxyMode.Manual, "", 0));
        Expect(ref failures, NetworkProxy.Instance.GetProxy(target) is null, "端点不完整应回落为直连而不是抛异常");

        NetworkProxy.Apply(ProxySnapshot.SystemDefault);
        Expect(ref failures, ProxySnapshot.SystemDefault.DisplayText == "跟随系统", "默认配置的文案应为「跟随系统」");
        return failures;
    }

    /// <summary>草稿校验:空主机/坏端口要拦下,并且失败不能把已生效配置改掉。</summary>
    private static int RejectInvalidDraft(string dir)
    {
        var failures = 0;
        var f = Create(dir);
        Expect(ref failures, f.Vm.ProxyModeIndex == 0, "首次打开草稿应落在「跟随系统」");

        f.Vm.ProxyModeIndex = 2; // 手动
        Expect(ref failures, f.Vm.IsManualProxy, "选中手动应展开手动配置区");

        f.Vm.ProxyHost = "   ";
        f.Vm.ProxyPortText = "7890";
        f.Vm.SaveProxySettingsCommand.Execute(null);
        Expect(ref failures, !string.IsNullOrEmpty(f.Vm.ProxyStatusText), "空主机应给出提示");
        Expect(ref failures, f.State.Proxy.Mode == ProxyMode.System, "无效草稿不得覆盖已生效配置");

        f.Vm.ProxyHost = "127.0.0.1";
        f.Vm.ProxyPortText = "0";
        f.Vm.SaveProxySettingsCommand.Execute(null);
        Expect(ref failures, !string.IsNullOrEmpty(f.Vm.ProxyStatusText), "端口 0 应给出提示");
        Expect(ref failures, f.State.Proxy.Mode == ProxyMode.System, "端口非法时也不得覆盖已生效配置");

        f.Vm.ProxyPortText = "70000";
        f.Vm.SaveProxySettingsCommand.Execute(null);
        Expect(ref failures, f.State.Proxy.Mode == ProxyMode.System, "端口越界时也不得覆盖已生效配置");
        return failures;
    }

    /// <summary>保存:剥协议前缀、应用全局出口、落盘状态同步。</summary>
    private static int SaveAppliesAndPersists(string dir)
    {
        var failures = 0;
        var f = Create(dir);

        f.Vm.ProxyModeIndex = 2;
        f.Vm.ProxyHost = "http://127.0.0.1:7890/"; // 整条地址粘进来
        f.Vm.ProxyPortText = "";
        f.Vm.ProxyUsername = " neo ";
        f.Vm.ProxyPassword = "secret";
        f.Vm.SaveProxySettingsCommand.Execute(null);

        Expect(ref failures, f.Vm.ProxyStatusText is null, $"合法配置不应留下错误提示,实际:{f.Vm.ProxyStatusText}");
        Expect(ref failures, f.State.Proxy.Mode == ProxyMode.Manual, "保存后落盘模式应为手动");
        Expect(ref failures, f.State.Proxy.Host == "127.0.0.1", $"应剥掉协议前缀,实际:{f.State.Proxy.Host}");
        Expect(ref failures, f.State.Proxy.Port == 7890, $"应从地址里拆出端口,实际:{f.State.Proxy.Port}");
        Expect(ref failures, f.State.Proxy.Username == "neo", $"用户名应 trim,实际:[{f.State.Proxy.Username}]");

        // 全局出口同步:不再需要重启或重建 HttpClient
        Expect(ref failures, NetworkProxy.Current.Mode == ProxyMode.Manual, "保存后全局出口应立即切到手动");
        Expect(ref failures, NetworkProxy.Current.Port == 7890, "全局出口端口应与保存值一致");
        Expect(ref failures, f.Vm.ProxySummary == "手动 · 127.0.0.1:7890",
            $"设置页副文案应跟随,实际:{f.Vm.ProxySummary}");

        // 切回直连
        f.Vm.ProxyModeIndex = 1;
        f.Vm.SaveProxySettingsCommand.Execute(null);
        Expect(ref failures, NetworkProxy.Current.Mode == ProxyMode.Direct, "切直连后全局出口应为直连");
        Expect(ref failures, f.Vm.ProxySummary == "不使用代理", $"直连文案,实际:{f.Vm.ProxySummary}");
        return failures;
    }

    /// <summary>落盘的文件重新加载后要恢复(进程重启路径)。</summary>
    private static int ReloadRestores(string dir)
    {
        var failures = 0;
        Directory.CreateDirectory(dir);
        var statePath = Path.Combine(dir, "state.json");

        var seed = new AppStateStore(statePath);
        seed.Proxy = new ProxySnapshot(ProxyMode.Manual, "10.0.0.2", 1080, "u", "p");
        seed.Save();

        var reloaded = new AppStateStore(statePath);
        Expect(ref failures, reloaded.Proxy.Mode == ProxyMode.Manual, "重载后模式应恢复");
        Expect(ref failures, reloaded.Proxy.Host == "10.0.0.2", "重载后主机应恢复");
        Expect(ref failures, reloaded.Proxy.Port == 1080, "重载后端口应恢复");
        Expect(ref failures, reloaded.Proxy.Password == "p", "重载后密码应恢复");
        return failures;
    }

    /// <summary>旧版本写的 state.json 里没有 Proxy* 字段:必须落在跟随系统(历史行为),不能变直连。</summary>
    private static int LegacyStateFallsBackToSystem(string dir)
    {
        var failures = 0;
        Directory.CreateDirectory(dir);
        var statePath = Path.Combine(dir, "state.json");
        File.WriteAllText(statePath, """{"Theme":"Dark","Volume":55}""");

        var state = new AppStateStore(statePath);
        Expect(ref failures, state.Proxy.Mode == ProxyMode.System, "旧配置应回落到跟随系统");
        Expect(ref failures, state.Proxy.Host.Length == 0, "旧配置不应带出主机");

        // 手改出越界的模式值也不能让应用走直连
        File.WriteAllText(statePath, """{"ProxyMode":9,"ProxyHost":"127.0.0.1","ProxyPort":7890}""");
        var corrupted = new AppStateStore(statePath);
        Expect(ref failures, corrupted.Proxy.Mode == ProxyMode.System, "未知模式值应回落跟随系统");
        return failures;
    }

    // ---- 夹具 ----

    private static MusicCacheService NewCache(string dir) =>
        new(64, Path.Combine(dir, "cache"), new HttpClient());

    private static (SettingsViewModel Vm, AppStateStore State) Create(string dir)
    {
        Directory.CreateDirectory(dir);
        var state = new AppStateStore(Path.Combine(dir, "state.json"));
        IAudioPlayer player = new HeadlessApp.StubAudioPlayer();
        var vm = new SettingsViewModel(state, NewCache(dir), player);
        vm.OpenProxySettingsCommand.Execute(null); // 走真实入口:载入草稿
        return (vm, state);
    }

    private static void Expect(ref int failures, bool condition, string message)
    {
        if (condition) return;
        failures++;
        Console.Error.WriteLine($"[proxy][FAIL] {message}");
    }
}
