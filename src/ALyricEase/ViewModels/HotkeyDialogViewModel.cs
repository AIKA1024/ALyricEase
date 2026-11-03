using ALyricEase.Services;
using ALyricEase.Services.Hotkeys;
using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ALyricEase.ViewModels;

/// <summary>快捷键分组(弹窗里按"播放控制/音量/导航"分段)。</summary>
public sealed record HotkeyGroupViewModel(string Title, IReadOnlyList<HotkeyItemViewModel> Items);

/// <summary>一条可改键的动作行。</summary>
public sealed partial class HotkeyItemViewModel : ViewModelBase
{
    private readonly HotkeyDialogViewModel _owner;
    private HotkeyGesture _gesture;
    private bool _isCapturing;

    internal HotkeyItemViewModel(HotkeyActionDefinition definition, HotkeyGesture gesture,
        HotkeyDialogViewModel owner)
    {
        Definition = definition;
        _gesture = gesture;
        _owner = owner;
    }

    public HotkeyActionDefinition Definition { get; }

    public string Title => Definition.Title;

    /// <summary>未改过时右侧按钮的悬停提示:告诉用户默认是什么,免得以为"没设置"。</summary>
    public string DefaultHint => $"默认 {Definition.Default.ToDisplayString()}";

    /// <summary>是否勾选了「应用在后台也响应」(注册成系统级热键)。</summary>
    public bool IsGlobal
    {
        get => _owner.IsGlobal(Definition.Id);
        set => _owner.SetGlobal(this, value);
    }

    /// <summary>平台是否支持系统级热键(Android 没有注册器,整列隐藏)。</summary>
    public bool IsGlobalSupported => _owner.IsGlobalSupported;

    /// <summary>勾了全局但系统没给(组合被其他程序占用)。</summary>
    public bool GlobalFailed => IsGlobal && _owner.IsGlobalFailed(Definition.Id);

    /// <summary>没有绑定、或组合是单键(空格)时不可勾选 —— 注册不了的东西别让点。</summary>
    public bool CanBeGlobal => HasBinding && GlobalHotkeyRules.CanRegister(_gesture);

    /// <summary>复选框的悬停说明:说明为什么能/不能全局。</summary>
    public string GlobalHint => !HasBinding
        ? "先给这个动作设置快捷键"
        : CanBeGlobal
            ? "应用在后台（切到其他程序或最小化到托盘）也响应"
            : $"「{_gesture.ToDisplayString()}」是单键；全局热键要带 Ctrl / Alt / Shift，或改用 Pause、媒体键";

    /// <summary>正在等待用户按键(按钮切到强调色并显示"请按键…")。</summary>
    public bool IsCapturing
    {
        get => _isCapturing;
        internal set
        {
            if (_isCapturing == value) return;
            _isCapturing = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(GestureText));
        }
    }

    public HotkeyGesture Gesture => _gesture;

    public bool HasBinding => !_gesture.IsEmpty;

    /// <summary>与出厂默认不同(行标题右侧打点,和"恢复默认"形成对照)。</summary>
    public bool IsModified => _gesture != Definition.Default;

    public string GestureText => _isCapturing
        ? "请按键…"
        : HasBinding ? _gesture.ToDisplayString() : "未设置";

    internal void SetGesture(HotkeyGesture gesture)
    {
        if (_gesture == gesture) return;
        _gesture = gesture;
        OnPropertyChanged(nameof(Gesture));
        OnPropertyChanged(nameof(HasBinding));
        OnPropertyChanged(nameof(IsModified));
        OnPropertyChanged(nameof(GestureText));
        NotifyGlobalChanged();
    }

    /// <summary>全局状态不在行 VM 里存(以 state 为准),变更后显式喊一次让绑定回读。</summary>
    internal void NotifyGlobalChanged()
    {
        OnPropertyChanged(nameof(IsGlobal));
        OnPropertyChanged(nameof(GlobalFailed));
        OnPropertyChanged(nameof(IsGlobalSupported));
        OnPropertyChanged(nameof(CanBeGlobal));
        OnPropertyChanged(nameof(GlobalHint));
    }

    [RelayCommand]
    private void Capture() => _owner.BeginCapture(this);

    [RelayCommand]
    private void Clear() => _owner.ClearBinding(this);
}

/// <summary>
/// 「键盘快捷键」弹窗 VM:列出 <see cref="HotkeyCatalog"/> 的全部动作,
/// 点击某行进入录音态,再按一次组合键即写回 state.json。
/// <para>
/// 写盘走 <see cref="AppStateStore.SetHotkey"/>:等于默认值就删覆盖项,
/// 所以"改回默认"不会在配置文件里留垃圾。<see cref="AppStateStore.HotkeysChanged"/>
/// 这里不订阅 —— 本弹窗是唯一的写入方,写完直接改行 VM 的状态即可。
/// </para>
/// </summary>
public sealed partial class HotkeyDialogViewModel : ViewModelBase
{
    private readonly AppStateStore _state;
    private readonly GlobalHotkeyHost? _globals;
    private HotkeyItemViewModel? _capturingItem;

    /// <param name="globals">系统级热键对账器;null 或未接平台注册器时,"全局"列不可用。</param>
    public HotkeyDialogViewModel(AppStateStore state, GlobalHotkeyHost? globals = null)
    {
        _state = state;
        _globals = globals;
        var items = HotkeyCatalog.Actions
            .Select(action => new HotkeyItemViewModel(action, state.ResolveHotkey(action.Id), this))
            .ToList();
        Items = items;
        Groups = items
            .GroupBy(item => item.Definition.Group)
            .Select(group => new HotkeyGroupViewModel(group.Key, group.ToList()))
            .ToList();
        // 注册失败集合会在对账后变化(启动时被占用、用户改键后换了个也被占用的组合)。
        if (_globals is not null) _globals.Changed += OnGlobalStateChanged;
    }

    /// <summary>平台是否支持系统级热键。</summary>
    public bool IsGlobalSupported => _globals?.IsSupported == true;

    internal bool IsGlobal(string actionId) => _state.IsGlobalHotkey(actionId);

    internal bool IsGlobalFailed(string actionId) => _globals?.IsFailed(actionId) == true;

    /// <summary>勾选 / 取消「全局」。勾选当场就注册,失败(被占用、组合不可注册)立刻回滚并提示。</summary>
    internal void SetGlobal(HotkeyItemViewModel item, bool value)
    {
        if (!IsGlobalSupported)
        {
            item.NotifyGlobalChanged();
            return;
        }

        var actionId = item.Definition.Id;
        if (value)
        {
            var gesture = item.Gesture;
            if (gesture.IsEmpty)
            {
                Hint = $"「{item.Title}」还没有绑定快捷键，先按一下组合键再勾选。";
                item.NotifyGlobalChanged();
                return;
            }

            if (!GlobalHotkeyRules.CanRegister(gesture))
            {
                Hint = $"「{gesture.ToDisplayString()}」是单键，全局热键要带 Ctrl / Alt / Shift，或改用 Pause、媒体键。";
                item.NotifyGlobalChanged();
                return;
            }

            _state.SetGlobalHotkey(actionId, true); // 触发对账
            if (_globals?.IsFailed(actionId) == true)
            {
                _state.SetGlobalHotkey(actionId, false);
                Hint = $"「{gesture.ToDisplayString()}」已被其他程序占用，没能注册成全局热键。";
            }
            else
            {
                Hint = $"「{item.Title}」已设为全局：切到其他程序也会响应。";
            }
        }
        else
        {
            _state.SetGlobalHotkey(actionId, false);
            Hint = $"「{item.Title}」只在应用窗口内生效。";
        }

        item.NotifyGlobalChanged();
    }

    private void OnGlobalStateChanged()
    {
        foreach (var item in Items) item.NotifyGlobalChanged();
    }

    public IReadOnlyList<HotkeyItemViewModel> Items { get; }

    public IReadOnlyList<HotkeyGroupViewModel> Groups { get; }

    /// <summary>提示行(冲突/不支持/已恢复默认);空则整行隐藏。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHint))]
    private string? _hint;

    public bool HasHint => !string.IsNullOrEmpty(Hint);

    /// <summary>有任意一行在录音。视图据此把键盘焦点交给捕获层。</summary>
    public bool IsCapturing => _capturingItem is not null;

    /// <summary>进入录音态(请求视图把焦点移到捕获层,避免空格顺手按到按钮)。</summary>
    public event Action? CaptureFocusRequested;

    /// <summary>每次打开弹窗重读配置:列表里显示的永远是当前真正生效的组合。</summary>
    public void Reload()
    {
        CancelCapture();
        Hint = null;
        foreach (var item in Items)
        {
            item.SetGesture(_state.ResolveHotkey(item.Definition.Id));
            item.NotifyGlobalChanged();
        }
    }

    internal void BeginCapture(HotkeyItemViewModel item)
    {
        if (ReferenceEquals(_capturingItem, item)) return;

        _capturingItem?.IsCapturing = false;
        _capturingItem = item;
        item.IsCapturing = true;
        OnPropertyChanged(nameof(IsCapturing));
        Hint = "按下组合键；Esc 取消，单按 Backspace / Delete 清除。";
        CaptureFocusRequested?.Invoke();
    }

    internal void ClearBinding(HotkeyItemViewModel item)
    {
        item.SetGesture(HotkeyGesture.None);
        _state.SetHotkey(item.Definition.Id, HotkeyGesture.None);
        item.NotifyGlobalChanged(); // 清绑定会连带收回全局标记
        if (ReferenceEquals(_capturingItem, item)) CancelCapture();
        Hint = $"已清除「{item.Title}」的快捷键。";
    }

    public void CancelCapture()
    {
        if (_capturingItem is null) return;
        _capturingItem.IsCapturing = false;
        _capturingItem = null;
        OnPropertyChanged(nameof(IsCapturing));
    }

    /// <summary>
    /// 录音态的按键入口(视图在隧道阶段调用)。返回 true 表示本次按键被吞掉 ——
    /// 否则 Tab 会跳焦点、空格会按下刚才那个按钮。
    /// </summary>
    public bool HandleCaptureKey(Key key, KeyModifiers modifiers)
    {
        if (_capturingItem is not { } item) return false;

        if (key == Key.Escape)
        {
            CancelCapture();
            Hint = "已取消改键。";
            return true;
        }

        if (key is (Key.Back or Key.Delete) && modifiers == KeyModifiers.None)
        {
            ClearBinding(item);
            return true;
        }

        // 单独按修饰键:用户还在凑组合,什么都不做(别提示、别结束录制)
        if (key is Key.None or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin)
        {
            return true;
        }

        if (modifiers.HasFlag(KeyModifiers.Meta))
        {
            Hint = "不支持 Win 组合键(系统保留),请换一个。";
            return true;
        }

        if (!HotkeyGesture.IsBindableKey(key))
        {
            Hint = "Tab 与锁定键不能用作快捷键,请换一个。";
            return true;
        }

        var gesture = HotkeyGesture.Create(key, modifiers);
        if (!gesture.IsValid)
        {
            Hint = "此组合键用于关闭窗口，请换一个。";
            return true;
        }
        var conflict = Items.FirstOrDefault(other =>
            !ReferenceEquals(other, item) && other.HasBinding && other.Gesture == gesture);
        if (conflict is not null)
        {
            // 保持录音态,让用户直接再按一个 —— 比"结束录制再点一次"少一步
            Hint = $"「{gesture.ToDisplayString()}」已被「{conflict.Title}」使用,请换一个。";
            return true;
        }

        item.SetGesture(gesture);
        _state.SetHotkey(item.Definition.Id, gesture);
        CancelCapture();
        Hint = gesture == item.Definition.Default
            ? $"「{item.Title}」已恢复默认快捷键。"
            : $"「{item.Title}」已改为 {gesture.ToDisplayString()}。";
        return true;
    }

    [RelayCommand]
    private void ResetAll()
    {
        _state.ResetHotkeys(); // 连带清掉全部全局标记
        CancelCapture();
        foreach (var item in Items)
        {
            item.SetGesture(item.Definition.Default);
            item.NotifyGlobalChanged();
        }
        Hint = "已恢复全部默认快捷键。";
    }
}
