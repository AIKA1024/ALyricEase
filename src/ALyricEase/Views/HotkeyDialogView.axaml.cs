using System;
using ALyricEase.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ALyricEase.Views;

public partial class HotkeyDialogView : UserControl
{
    private HotkeyDialogViewModel? _hotkeys;
    private TopLevel? _topLevel;
    private InputElement? _previousFocus;

    private bool IsActive => IsEffectivelyVisible && IsEffectivelyEnabled &&
        DataContext is MainViewModel { IsHotkeyDialogOpen: true, IsErrorDialogOpen: false };

    public HotkeyDialogView()
    {
        InitializeComponent();
        // 隧道阶段:先于焦点控件拿到按键。录音态下必须这样,否则空格会同时被
        // "组合键按钮"当成点击(它刚被点过、焦点还在上面),录进去的是按钮动作。
        DataContextChanged += OnDataContextChanged;
        AttachedToVisualTree += (_, _) =>
        {
            _topLevel = TopLevel.GetTopLevel(this);
            _topLevel?.AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
            _topLevel?.AddHandler(GotFocusEvent, OnModalGotFocus, RoutingStrategies.Bubble);
            if (IsActive) FocusOnOpen();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _topLevel?.RemoveHandler(KeyDownEvent, OnPreviewKeyDown);
            _topLevel?.RemoveHandler(GotFocusEvent, OnModalGotFocus);
            _topLevel = null;
        };
        PropertyChanged += (_, e) =>
        {
            if (e.Property != IsVisibleProperty) return;
            if (IsVisible) FocusOnOpen();
            else
            {
                if (_previousFocus is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true })
                    _previousFocus.Focus();
                _previousFocus = null;
            }
        };
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_hotkeys is not null) _hotkeys.CaptureFocusRequested -= OnCaptureFocusRequested;
        _hotkeys = (DataContext as MainViewModel)?.Hotkeys;
        if (_hotkeys is not null) _hotkeys.CaptureFocusRequested += OnCaptureFocusRequested;
    }

    /// <summary>进入录音态后把焦点移到捕获层。延后一帧:本次点击还在按钮的处理栈里,
    /// 立刻改焦点会被弹回。</summary>
    private void OnCaptureFocusRequested()
        => Dispatcher.UIThread.Post(() =>
        {
            if (IsActive && _hotkeys?.IsCapturing == true) CaptureSurface.Focus();
        });

    private void FocusOnOpen()
    {
        _previousFocus = _topLevel?.FocusManager?.GetFocusedElement() as InputElement;
        Dispatcher.UIThread.Post(() =>
        {
            if (IsActive) DoneButton.Focus();
        }, DispatcherPriority.Loaded);
    }

    private void OnModalGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (IsActive && e.Source is Visual source && !this.IsVisualAncestorOf(source))
            DoneButton.Focus();
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        // 弹窗隐藏(含淡出期间)不接管任何按键,把 Esc 等留给壳层
        if (!IsActive || _hotkeys is null) return;
        if (_hotkeys.HandleCaptureKey(e.Key, e.KeyModifiers))
        {
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape)
        {
            ((MainViewModel)DataContext!).CloseHotkeyDialogCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Source is not Visual source || !this.IsVisualAncestorOf(source))
        {
            e.Handled = true;
            DoneButton.Focus();
        }
    }
}
