using ALyricEase.Infrastructure;
using ALyricEase.Infrastructure.Logging;
using ALyricEase.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging;

namespace ALyricEase.Services.Hotkeys;

/// <summary>
/// 应用内快捷键分发(窗口级,不是系统级全局热键)。
/// <para>
/// 由桌面壳层挂在主窗口的 KeyDown **隧道**阶段:先于焦点控件拿到按键,
/// 命中就 <c>Handled</c> —— 否则空格会同时"暂停播放"并"按下当前聚焦的按钮"。
/// </para>
/// <para>
/// 有意不在下列情况分发:
/// ① 焦点在文本输入框(用户在打字,空格/Ctrl+← 属于编辑操作);
/// ② 有模态弹层打开(否则后台会莫名其妙开始/暂停);
/// ③ 带 Win 的组合(系统保留,抢不到也不该抢)。
/// </para>
/// </summary>
public sealed class HotkeyService
{
    private readonly AppStateStore _state;
    private readonly Func<bool>? _isBlocked;

    /// <param name="state">快捷键配置来源(每次按键现查,改键立即生效)。</param>
    /// <param name="isBlocked">是否有模态弹层打开;null = 永不阻塞。</param>
    public HotkeyService(AppStateStore state, Func<bool>? isBlocked = null)
    {
        _state = state;
        _isBlocked = isBlocked;
    }

    /// <summary>焦点在文本输入控件(或其模板内部)时不接管按键。</summary>
    public static bool IsTextInputFocused(IInputElement? focused) => focused switch
    {
        null => false,
        TextBox => true,
        Visual visual => visual.FindAncestorOfType<TextBox>() is not null,
        _ => false,
    };

    /// <summary>尝试把本次按键当作快捷键处理;处理了返回 true 并置 <c>Handled</c>。</summary>
    public bool TryHandle(IInputElement? focused, KeyEventArgs e)
    {
        if (e.Handled) return false;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Meta)) return false;
        if (IsTextInputFocused(focused)) return false;
        if (_isBlocked?.Invoke() == true) return false;

        foreach (var action in HotkeyCatalog.Actions)
        {
            var gesture = _state.ResolveHotkey(action.Id);
            if (!gesture.Matches(e.Key, e.KeyModifiers)) continue;

            e.Handled = true;
            _ = ExecuteAsync(action.Id);
            return true;
        }

        return false;
    }

    private static async Task ExecuteAsync(string actionId)
    {
        try
        {
            var player = ServiceLocator.Get<PlayerViewModel>();
            switch (actionId)
            {
                case HotkeyCatalog.PlayPause:
                    player.TogglePlayPauseCommand.Execute(null);
                    break;
                case HotkeyCatalog.PreviousTrack:
                    await player.PlayPreviousAsync();
                    break;
                case HotkeyCatalog.NextTrack:
                    await player.PlayNextAsync();
                    break;
                case HotkeyCatalog.SeekBackward:
                    player.SeekTo(Math.Max(0, player.PositionMs - HotkeyCatalog.SeekStepMs));
                    break;
                case HotkeyCatalog.SeekForward:
                    player.SeekTo(player.PositionMs + HotkeyCatalog.SeekStepMs);
                    break;
                case HotkeyCatalog.VolumeUp:
                    player.Volume = Math.Min(100, player.Volume + HotkeyCatalog.VolumeStep);
                    break;
                case HotkeyCatalog.VolumeDown:
                    player.Volume = Math.Max(0, player.Volume - HotkeyCatalog.VolumeStep);
                    break;
                case HotkeyCatalog.CyclePlaybackMode:
                    player.TogglePlaybackModeCommand.Execute(null);
                    break;
                case HotkeyCatalog.OpenSearch:
                    var searchHost = ServiceLocator.Get<MainViewModel>();
                    searchHost.CloseNowPlayingCommand.Execute(null);
                    searchHost.GoSearchCommand.Execute(null);
                    break;
                case HotkeyCatalog.ToggleNowPlaying:
                    if (ServiceLocator.Get<MainViewModel>() is { } main)
                    {
                        if (main.ShowNowPlaying) main.CloseNowPlayingCommand.Execute(null);
                        else main.OpenNowPlayingCommand.Execute(null);
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            AppLogging.Factory.CreateLogger<HotkeyService>()
                .LogWarning(ex, "Hotkey action failed. ActionId={ActionId}", actionId);
        }
    }
}
