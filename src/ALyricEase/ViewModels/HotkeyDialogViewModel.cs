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
    private HotkeyItemViewModel? _capturingItem;

    public HotkeyDialogViewModel(AppStateStore state)
    {
        _state = state;
        var items = HotkeyCatalog.Actions
            .Select(action => new HotkeyItemViewModel(action, state.ResolveHotkey(action.Id), this))
            .ToList();
        Items = items;
        Groups = items
            .GroupBy(item => item.Definition.Group)
            .Select(group => new HotkeyGroupViewModel(group.Key, group.ToList()))
            .ToList();
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
            item.SetGesture(_state.ResolveHotkey(item.Definition.Id));
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
        _state.ResetHotkeys();
        CancelCapture();
        foreach (var item in Items) item.SetGesture(item.Definition.Default);
        Hint = "已恢复全部默认快捷键。";
    }
}
