using ALyricEase.ViewModels;
using ALyricEase.Views;
using ALyricEase.Infrastructure;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using System.Runtime.InteropServices;

namespace ALyricEase.Services;

/// <summary>桌面窗口、模态关闭决策和应用级托盘的生命周期。</summary>
internal sealed class DesktopLifecycleController : IDisposable
{
    private readonly Window _window;
    private readonly SettingsViewModel _settings;
    private readonly AppStateStore _state;
    private readonly TrayIcon _tray;
    private readonly ContentControl _dialogHost;
    private readonly List<(Control Control, bool WasEnabled)> _backgroundControls = new();
    private IInputElement? _previousFocus;
    private CloseBehaviorDialogView? _dialog;
    private bool _promptPending;
    private bool _exiting;
    private bool _disposed;
    private WindowState _visibleWindowState = WindowState.Normal;

    public DesktopLifecycleController(Application app, Window window, SettingsViewModel settings,
        AppStateStore state, ContentControl dialogHost)
    {
        _window = window;
        _settings = settings;
        _state = state;
        _dialogHost = dialogHost;

        using var iconStream = AssetLoader.Open(new Uri("avares://ALyricEase/Assets/app.ico"));
        _tray = new TrayIcon
        {
            Icon = new WindowIcon(iconStream),
            ToolTipText = "ALyricEase",
            IsVisible = true,
            Command = new RelayCommand(RestoreWindow),
            Menu = new NativeMenu
            {
                new NativeMenuItem("显示主窗口") { Command = new RelayCommand(RestoreWindow) },
                new NativeMenuItemSeparator(),
                new NativeMenuItem("退出应用") { Command = new RelayCommand(Exit) },
            },
        };
        TrayIcon.SetIcons(app, new TrayIcons { _tray });
        _window.Closing += OnClosing;
        _window.AddHandler(InputElement.KeyDownEvent, OnModalKeyDown, RoutingStrategies.Tunnel);
        _window.AddHandler(InputElement.GotFocusEvent, OnModalGotFocus, RoutingStrategies.Bubble);
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        // 系统关机、应用显式关闭和托盘退出必须真正结束进程。
        if (_exiting || e.CloseReason is not (WindowCloseReason.WindowClosing or WindowCloseReason.Undefined))
            return;
        if (_promptPending)
        {
            e.Cancel = true;
            _dialog?.FocusDefaultAction();
            return;
        }

        switch (_state.MinimizeToTrayOnClose)
        {
            case false:
                return;
            case true:
                e.Cancel = true;
                Dispatcher.UIThread.Post(MinimizeToTray);
                return;
            default:
                e.Cancel = true;
                _promptPending = true;
                // 等本次 Closing 返回后再打开模态窗口，避免关闭栈内的重入。
                Dispatcher.UIThread.Post(PromptForCloseBehavior);
                return;
        }
    }

    private async void PromptForCloseBehavior()
    {
        if (_exiting || _disposed) return;
        try
        {
            _dialog = new CloseBehaviorDialogView();
            _previousFocus = _window.FocusManager?.GetFocusedElement();
            _dialogHost.Content = _dialog;
            _dialogHost.IsVisible = true;
            DisableBackground();
            ModalVisibilityTransition.SetIsOpen(_dialog, true);
            var choice = await _dialog.Completion;
            ModalVisibilityTransition.SetIsOpen(_dialog, false);
            // 复用相同的淡出完成信号；窗口隐藏/进程退出前，动画必须有机会完整播放。
            await ModalVisibilityTransition.WaitForTransitionAsync(_dialog);
            if (_exiting || _disposed || choice is null) return;

            if (choice.Remember)
                _settings.CloseBehaviorIndex = choice.MinimizeToTray ? 1 : 2;
            if (choice.MinimizeToTray)
                MinimizeToTray();
            else
                Exit();
        }
        finally
        {
            _dialogHost.Content = null;
            _dialogHost.IsVisible = false;
            foreach (var (control, wasEnabled) in _backgroundControls)
                control.SetCurrentValue(Control.IsEnabledProperty, wasEnabled);
            _backgroundControls.Clear();
            _dialog = null;
            _promptPending = false;
            if (!_exiting && !_disposed && _window.IsVisible) _previousFocus?.Focus();
            _previousFocus = null;
        }
    }

    private void DisableBackground()
    {
        if (_dialogHost.Parent is not Panel panel)
            throw new InvalidOperationException("关闭弹层宿主必须位于主窗口的 Panel 中");
        foreach (var control in panel.Children)
        {
            if (control == _dialogHost) continue;
            _backgroundControls.Add((control, control.IsEnabled));
            control.SetCurrentValue(Control.IsEnabledProperty, false);
        }
    }

    private void OnModalKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_promptPending || _dialog is null) return;
        if (e.Key == Key.Escape)
        {
            if (ModalVisibilityTransition.GetIsOpen(_dialog))
                _dialog.ViewModel.CancelCommand.Execute(null);
            e.Handled = true;
            return;
        }
        // 保留弹窗自身的 Tab/Space/Enter，禁止背景快捷键和淡出期间的输入。
        if (!_dialog.IsEnabled || (e.KeyModifiers != KeyModifiers.None && e.Key != Key.Tab) ||
            e.Source is not Control source || !_dialog.IsVisualAncestorOf(source))
        {
            e.Handled = true;
            _dialog.FocusDefaultAction();
        }
    }

    private void OnModalGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (_promptPending && _dialog is { IsVisible: true, IsEnabled: true } &&
            e.Source is Control control && !_dialog.IsVisualAncestorOf(control))
            _dialog.FocusDefaultAction();
    }

    private void MinimizeToTray()
    {
        if (_exiting || _disposed) return;
        if (_window.WindowState != WindowState.Minimized)
            _visibleWindowState = _window.WindowState;
        _state.Flush();
        _window.Hide();
    }

    internal void RestoreWindow()
    {
        if (_exiting || _disposed) return;
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = _visibleWindowState;
        _window.Show();
        // 托盘点击/菜单命令返回后再激活，避免菜单收起时又把前台交回 Explorer。
        Dispatcher.UIThread.Post(() =>
        {
            if (_exiting || _disposed || !_window.IsVisible) return;
            var handle = _window.TryGetPlatformHandle();
            if (OperatingSystem.IsWindows() && handle is { HandleDescriptor: "HWND" } && handle.Handle != IntPtr.Zero)
            {
                // Win32 的实际最小化状态也要还原；仅 Activate 不会展开最小化窗口。
                if (IsIconic(handle.Handle)) ShowWindow(handle.Handle, 9 /* SW_RESTORE */);
                // SetForegroundWindow 可能被系统拒绝；仍把窗口抬到普通窗口最前面，确保可见。
                SetWindowPos(handle.Handle, IntPtr.Zero /* HWND_TOP */, 0, 0, 0, 0,
                    0x0001 | 0x0002 | 0x0010 /* NOSIZE | NOMOVE | NOACTIVATE */);
            }
            _window.Activate();
            _dialog?.FocusDefaultAction();
        }, DispatcherPriority.Background);
    }

    internal void Exit()
    {
        if (_exiting || _disposed) return;
        _exiting = true;
        _state.Flush();
        _dialog?.ViewModel.CancelCommand.Execute(null);
        _window.Close();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _window.Closing -= OnClosing;
        _window.RemoveHandler(InputElement.KeyDownEvent, OnModalKeyDown);
        _window.RemoveHandler(InputElement.GotFocusEvent, OnModalGotFocus);
        _dialog?.ViewModel.CancelCommand.Execute(null);
        _tray.Dispose();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
