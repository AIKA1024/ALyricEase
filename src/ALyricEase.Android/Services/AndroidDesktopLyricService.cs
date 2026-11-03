using System.ComponentModel;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Provider;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using ALyricEase.Services.Smtc;
using ALyricEase.ViewModels;
using Microsoft.Extensions.Logging;

namespace ALyricEase.Services;

/// <summary>使用应用 Context 持有悬浮窗，Activity 重建不会泄漏旧 Activity。
/// 后台存活复用现有媒体前台服务；媒体服务停止且应用不在前台时移除窗口。</summary>
public sealed class AndroidDesktopLyricService : IDisposable
{
    private readonly DesktopLyricViewModel _model;
    private readonly AppStateStore _state;
    private readonly ILogger<AndroidDesktopLyricService> _logger;
    private readonly Context _context = global::Android.App.Application.Context;
    private readonly Handler _handler = new(Looper.MainLooper!);
    private IWindowManager? _manager;
    private LinearLayout? _root;
    private LinearLayout? _toolbar;
    private ToggleButton? _translationButton;
    private TextView? _original;
    private TextView? _translation;
    private WindowManagerLayoutParams? _layout;
    private bool _foreground;
    private bool _awaitingPermission;
    private bool _disposed;
    private float _downX, _downY;
    private int _startX, _startY;
    private int _collapsedToolbarHeight;

    public AndroidDesktopLyricService(DesktopLyricViewModel model, AppStateStore state,
        ILogger<AndroidDesktopLyricService> logger)
    {
        _model = model;
        _state = state;
        _logger = logger;
        _model.PropertyChanged += OnChanged;
    }

    public void Resume() => _handler.Post(() =>
    {
        _foreground = true;
        _awaitingPermission = false;
        Refresh();
    });

    public void Background() => _handler.Post(() => { _foreground = false; Refresh(); });
    public void Refresh() => Render(); // Android 生命周期回调均位于主线程。

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(DesktopLyricViewModel.Enabled)
            or nameof(DesktopLyricViewModel.Original) or nameof(DesktopLyricViewModel.Translation)
            or nameof(DesktopLyricViewModel.FontSize) or nameof(DesktopLyricViewModel.BackgroundOpacity)
            or nameof(DesktopLyricViewModel.ClickThrough) or nameof(DesktopLyricViewModel.ShowTranslation)
            or nameof(DesktopLyricViewModel.IsWindowVisible))) return;
        var requestPermission = e.PropertyName == nameof(DesktopLyricViewModel.Enabled) && _model.Enabled;
        _handler.Post(() =>
        {
            if (_disposed) return;
            if (requestPermission && _model.Enabled && !Settings.CanDrawOverlays(_context))
                RequestPermission();
            Render();
        });
    }

    private void RequestPermission()
    {
        if (_awaitingPermission) return;
        try
        {
            _awaitingPermission = true;
            _model.Hint = "请在系统设置中允许 ALyricEase 显示在其他应用上层，然后返回应用。";
            var intent = new Intent(Settings.ActionManageOverlayPermission,
                global::Android.Net.Uri.Parse("package:" + _context.PackageName));
            intent.AddFlags(ActivityFlags.NewTask);
            _context.StartActivity(intent);
        }
        catch (Exception ex)
        {
            Fail("无法打开悬浮窗设置，请在系统应用权限中允许显示在其他应用上层后重试。", ex);
        }
    }

    private void Render()
    {
        if (_disposed) return;
        if (!_model.Enabled)
        {
            _awaitingPermission = false;
            Hide();
            return;
        }
        // 暂停收起:只摘窗口,不清权限状态;继续播放后按保存的位置重建。
        if (!_model.IsWindowVisible)
        {
            Hide();
            return;
        }
        if (!Settings.CanDrawOverlays(_context))
        {
            Hide();
            if (!_awaitingPermission)
            {
                _model.Hint = "未获得悬浮窗权限。打开开关可前往系统授权。";
                _model.Enabled = false;
            }
            return;
        }
        if (!_foreground && !MediaPlaybackService.IsForeground) { Hide(); return; }
        try
        {
            if (_root is null) CreateWindow();
            if (_root!.Background is GradientDrawable background)
                background.SetColor(Color.Argb((int)(255 * _model.BackgroundOpacity), 35, 39, 45));
            // 工具栏固定 48dp：收起高度，同时补偿窗口 Y，保持歌词屏幕坐标不变。
            var collapsedHeight = _model.ClickThrough ? Dp(48) : 0;
            var heightDelta = collapsedHeight - _collapsedToolbarHeight;
            var expectedHeight = _root.Height > 0 ? Math.Max(1, _root.Height - heightDelta) : (int?)null;
            _layout!.Y += heightDelta;
            _collapsedToolbarHeight = collapsedHeight;
            _toolbar!.Visibility = _model.ClickThrough ? ViewStates.Gone : ViewStates.Visible;
            _translationButton!.Checked = _model.ShowTranslation;
            _translationButton.SetTextColor(_model.ShowTranslation ? Color.Rgb(114, 229, 211) : Color.White);
            _layout!.Flags = WindowManagerFlags.NotFocusable | WindowManagerFlags.NotTouchModal
                | (_model.ClickThrough ? WindowManagerFlags.NotTouchable : 0);
            // Android 12+ 的非可信悬浮窗触摸穿透要求遮挡透明度不超过 0.8。
            var maxOpacity = OperatingSystem.IsAndroidVersionAtLeast(31)
                ? (_context.GetSystemService(Context.InputService) as global::Android.Hardware.Input.InputManager)?.MaximumObscuringOpacityForTouch ?? 0.8f
                : 0.8f;
            _layout.Alpha = _model.ClickThrough ? Math.Min(0.8f, maxOpacity) : 1f;
            _original!.Text = _model.Original;
            _original.TextSize = (float)_model.FontSize;
            _translation!.Text = _model.Translation;
            _translation.TextSize = (float)_model.TranslationFontSize;
            _translation.Visibility = _model.HasTranslation ? ViewStates.Visible : ViewStates.Gone;
            _model.Hint = "已允许悬浮窗；拖动歌词调整位置，点击 × 关闭。";
            ClampPosition(expectedHeight);
            _manager!.UpdateViewLayout(_root, _layout);
            _root!.Post(() =>
            {
                if (_root is null || _disposed) return;
                try { ClampPosition(); _manager!.UpdateViewLayout(_root, _layout); }
                catch (Exception ex) { Fail("悬浮歌词显示失败，请重新开启。", ex); }
            });
        }
        catch (Exception ex) { Fail("悬浮歌词显示失败，请检查悬浮窗权限后重试。", ex); }
    }

    private int Dp(float value) => (int)(value * (_context.Resources?.DisplayMetrics?.Density ?? 1));

    private void CreateWindow()
    {
        _manager = _context.GetSystemService(Context.WindowService)?.JavaCast<IWindowManager>()
            ?? throw new InvalidOperationException("WindowManager unavailable");
        _root = new LinearLayout(_context) { Orientation = Orientation.Vertical };
        _root.Touch += OnDrag;
        _root.SetPadding(Dp(12), Dp(8), Dp(4), Dp(8));
        var background = new GradientDrawable();
        background.SetColor(Color.Argb(220, 35, 39, 45));
        background.SetCornerRadius(Dp(12));
        _root.Background = background;
        var text = new LinearLayout(_context) { Orientation = Orientation.Vertical };
        _original = new TextView(_context) { Gravity = GravityFlags.Center };
        _original.SetTextColor(Color.Rgb(114, 229, 211));
        _original.SetShadowLayer(Dp(2), 0, Dp(1), Color.Black);
        _original.SetMaxLines(3);
        _original.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End;
        _translation = new TextView(_context) { Gravity = GravityFlags.Center };
        _translation.SetTextColor(Color.White);
        _translation.SetShadowLayer(Dp(2), 0, Dp(1), Color.Black);
        _translation.SetMaxLines(2);
        _translation.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End;
        text.AddView(_original);
        text.AddView(_translation);
        text.Touch += OnDrag;
        _original.Touch += OnDrag;
        _translation.Touch += OnDrag;
        _toolbar = new LinearLayout(_context) { Orientation = Orientation.Horizontal };
        _toolbar.SetGravity(GravityFlags.Right);
        _toolbar.Touch += OnDrag;
        void AddAction(string label, string description, Action action)
        {
            var button = new Button(_context) { Text = label, ContentDescription = description, TextSize = 12 };
            button.SetPadding(0, 0, 0, 0);
            button.SetTextColor(Color.White);
            button.SetBackgroundColor(Color.Argb(210, 50, 55, 62));
            button.Click += (_, _) => action();
            _toolbar.AddView(button, new LinearLayout.LayoutParams(0, Dp(48), 1));
        }
        AddAction("A−", "缩小字号", () => _model.DecreaseFontCommand.Execute(null));
        AddAction("A+", "放大字号", () => _model.IncreaseFontCommand.Execute(null));
        _translationButton = new ToggleButton(_context)
        {
            TextOn = "译", TextOff = "译", TextSize = 12,
            ContentDescription = "显示翻译", Checked = _model.ShowTranslation,
        };
        _translationButton.SetPadding(0, 0, 0, 0);
        _translationButton.SetBackgroundColor(Color.Argb(210, 50, 55, 62));
        _translationButton.CheckedChange += (_, e) => _model.ShowTranslation = e.IsChecked;
        _toolbar.AddView(_translationButton, new LinearLayout.LayoutParams(0, Dp(48), 1));
        AddAction("背景", "切换透明背景；更多选项在应用设置", () => _model.ToggleBackgroundCommand.Execute(null));
        AddAction("穿透", "开启触摸穿透，在应用设置中解除", () => _model.ClickThrough = true);
        var close = new Button(_context) { Text = "×", ContentDescription = "关闭桌面歌词" };
        close.SetTextColor(Color.White);
        close.SetBackgroundColor(Color.Transparent);
        close.Click += (_, _) => _model.Enabled = false;
        _toolbar.AddView(close, new LinearLayout.LayoutParams(Dp(48), Dp(48)));
        _root.AddView(_toolbar);
        _root.AddView(text);
        _layout = new WindowManagerLayoutParams(
            Dp(360), ViewGroup.LayoutParams.WrapContent,
            OperatingSystem.IsAndroidVersionAtLeast(26) ? WindowManagerTypes.ApplicationOverlay : WindowManagerTypes.Phone,
            WindowManagerFlags.NotFocusable | WindowManagerFlags.NotTouchModal,
            Format.Translucent)
        {
            Gravity = GravityFlags.Top | GravityFlags.Left,
            X = _state.AndroidLyricX ?? Dp(12),
            Y = _state.AndroidLyricY ?? Dp(100),
        };
        ClampPosition();
        _manager.AddView(_root, _layout);
    }

    private void ClampPosition(int? expectedHeight = null)
    {
        if (_layout is null) return;
        var metrics = _context.Resources!.DisplayMetrics!;
        _layout.Width = Math.Min(Dp(480), Math.Max(Dp(120), metrics.WidthPixels - Dp(16)));
        _layout.X = Math.Clamp(_layout.X, 0, Math.Max(0, metrics.WidthPixels - _layout.Width));
        var height = expectedHeight ?? (_root?.Height > 0 ? _root.Height : Dp(100));
        _layout.Y = Math.Clamp(_layout.Y, 0,
            Math.Max(0, metrics.HeightPixels - height - Dp(48)));
    }

    private void OnDrag(object? sender, global::Android.Views.View.TouchEventArgs e)
    {
        if (_layout is null || e.Event is not { } motion) return;
        switch (motion.ActionMasked)
        {
            case MotionEventActions.Down:
                _downX = motion.RawX; _downY = motion.RawY;
                _startX = _layout.X; _startY = _layout.Y;
                break;
            case MotionEventActions.Move:
                _layout.X = _startX + (int)(motion.RawX - _downX);
                _layout.Y = _startY + (int)(motion.RawY - _downY);
                try { ClampPosition(); _manager!.UpdateViewLayout(_root, _layout); }
                catch (Exception ex) { Fail("悬浮歌词移动失败，请重新开启。", ex); }
                break;
            case MotionEventActions.Up:
            case MotionEventActions.Cancel:
                _state.AndroidLyricX = _layout.X;
                _state.AndroidLyricY = _layout.Y - _collapsedToolbarHeight;
                _state.Save();
                break;
        }
        e.Handled = true;
    }

    private void Fail(string message, Exception exception)
    {
        _logger.LogWarning(exception, "Desktop lyric overlay failed");
        _awaitingPermission = false;
        _model.Hint = message;
        _model.Enabled = false;
        Hide();
    }

    private void Hide()
    {
        if (_root is null) return;
        try { _manager?.RemoveView(_root); }
        catch (Java.Lang.IllegalArgumentException) { /* 系统已移除窗口（例如撤销权限）。 */ }
        _root.Dispose();
        _root = null;
        _toolbar = null;
        _translationButton = null;
        _original = null;
        _translation = null;
        _layout = null;
        _collapsedToolbarHeight = 0;
    }

    public void Dispose()
    {
        _disposed = true;
        _model.PropertyChanged -= OnChanged;
        _handler.Post(Hide);
    }
}
