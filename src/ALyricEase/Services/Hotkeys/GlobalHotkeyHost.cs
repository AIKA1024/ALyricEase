using ALyricEase.Infrastructure.Logging;
using Microsoft.Extensions.Logging;

namespace ALyricEase.Services.Hotkeys;

/// <summary>
/// 「哪些动作在应用外也生效」的对账器:以 <see cref="AppStateStore.IsGlobalHotkey"/> 的意图为准,
/// 把它同步成系统里的实际注册状态。
/// <para>
/// 只在 <see cref="AppStateStore.HotkeysChanged"/> 时全量对账(改键 / 清除 / 勾选全局 / 恢复默认
/// 都会走到),不做按键期查询 —— 注册是有副作用的系统调用,不能每次按键都做一遍。
/// </para>
/// <para>
/// 注册失败不算致命:该动作退回"仅应用内生效",同时记进 <see cref="IsFailed"/> 供界面提示,
/// 下次对账(比如用户改键)会再试一次。平台不支持(Android)时 <see cref="IsSupported"/> 为 false。
/// </para>
/// </summary>
public sealed class GlobalHotkeyHost
{
    private readonly AppStateStore _state;
    private readonly Dictionary<string, HotkeyGesture> _registered = new(StringComparer.Ordinal);
    private readonly HashSet<string> _failed = new(StringComparer.Ordinal);
    private IGlobalHotkeyRegistrar? _registrar;
    private bool _syncing;

    public GlobalHotkeyHost(AppStateStore state)
    {
        _state = state;
        _state.HotkeysChanged += Sync;
    }

    /// <summary>平台是否支持系统级热键(Android 没有注册器)。</summary>
    public bool IsSupported => _registrar is not null;

    /// <summary>对账结果变化(注册失败集合变动、或刚接上注册器)。界面据此刷新行状态。</summary>
    public event Action? Changed;

    /// <summary>该动作当前是否"想要全局但没注册上"(被其他程序占用)。</summary>
    public bool IsFailed(string actionId) => _failed.Contains(actionId);

    /// <summary>接上平台注册器并立即对账(窗口已创建、有 HWND 之后才能接)。</summary>
    public void Attach(IGlobalHotkeyRegistrar registrar)
    {
        _registrar = registrar;
        _registered.Clear();
        _failed.Clear();
        Sync();
        Changed?.Invoke();
    }

    /// <summary>按当前配置补注册 / 撤销。</summary>
    public void Sync()
    {
        if (_registrar is null || _syncing) return;
        _syncing = true;
        try
        {
            var failuresChanged = false;

            // ① 先撤销:动作不再要全局、组合已改、或绑定被清空 —— 三种情况都得让出旧组合。
            foreach (var (actionId, gesture) in _registered.ToArray())
            {
                var want = Wanted(actionId);
                if (want is { } desired && desired == gesture) continue;
                _registrar.Unregister(actionId);
                _registered.Remove(actionId);
            }

            // ② 再注册:想全局、有绑定、且组合满足系统要求。
            foreach (var action in HotkeyCatalog.Actions)
            {
                if (_registered.ContainsKey(action.Id)) continue;
                if (Wanted(action.Id) is not { } gesture) continue;
                if (_registrar.TryRegister(action.Id, gesture))
                    _registered[action.Id] = gesture;
                else
                    failuresChanged |= _failed.Add(action.Id);
            }

            // ③ 收起已经不需要全局的失败记录,别让界面一直挂着警告。
            foreach (var actionId in _failed.ToArray())
            {
                if (Wanted(actionId) is not null) continue;
                _failed.Remove(actionId);
                failuresChanged = true;
            }

            if (failuresChanged)
            {
                // 用户报"勾了全局没反应"时,这条日志能直接指出是被占用还是没勾上。
                AppLogging.Factory.CreateLogger<GlobalHotkeyHost>().LogWarning(
                    "Global hotkey registration changed. Failed={Failed}", string.Join(',', _failed));
                Changed?.Invoke();
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>该动作期望的全局组合;null = 不该注册(没勾、没绑定、或不满足系统要求)。</summary>
    private HotkeyGesture? Wanted(string actionId)
    {
        if (!_state.IsGlobalHotkey(actionId)) return null;
        var gesture = _state.ResolveHotkey(actionId);
        return GlobalHotkeyRules.CanRegister(gesture) ? gesture : null;
    }

    /// <summary>退出前释放系统热键(进程被强杀时系统也会回收,但正常退出不该留下占用)。</summary>
    public void Dispose()
    {
        _state.HotkeysChanged -= Sync;
        _registrar?.UnregisterAll();
        _registered.Clear();
    }
}
