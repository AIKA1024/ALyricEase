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
    private TextView? _original;
    private TextView? _translation;
    private WindowManagerLayoutParams? _layout;
    private bool _foreground;
    private bool _awaitingPermission;
    private bool _disposed;
    private float _downX, _downY;
    private int _startX, _startY;

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
            or nameof(DesktopLyricViewModel.TextColor) or nameof(DesktopLyricViewModel.FontFamilyName)
            or nameof(DesktopLyricViewModel.ShadowEnabled) or nameof(DesktopLyricViewModel.ShadowBlur)
            or nameof(DesktopLyricViewModel.ShadowColor)
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
            ApplyTextStyle();
            FitWindowWidth();
            _model.Hint = "已允许悬浮窗；拖动可调整位置，字号/翻译/背景/穿透在应用设置里调整。";
            ClampPosition();
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

    /// <summary>
    /// 字体 / 文字颜色 / 阴影:和桌面端读同一份设置。
    /// ⚠ 描边在 Android 这边做不了(原生 TextView 没有文字描边),"文字描边"卡片在该端整个隐藏
    /// (见 <see cref="DesktopLyricViewModel.SupportsOutline"/>)。
    /// </summary>
    private void ApplyTextStyle()
    {
        if (_original is null || _translation is null) return;
        _original.SetTextColor(ToAndroidColor(_model.TextColor));
        // 翻译行固定近白(和桌面端一致),主歌词换成什么颜色都还能分出主次。
        _translation.SetTextColor(Color.Argb(230, 255, 255, 255));

        var family = _model.FontFamilyName;
        var typeface = string.IsNullOrWhiteSpace(family)
            ? null : Typeface.Create(family, TypefaceStyle.Normal);
        _original.SetTypeface(typeface, TypefaceStyle.Normal);
        _translation.SetTypeface(typeface, TypefaceStyle.Normal);

        if (_model.ShadowEnabled && _model.ShadowBlur > 0)
        {
            var color = ToAndroidColor(_model.ShadowColor);
            var radius = Dp((float)_model.ShadowBlur);
            _original.SetShadowLayer(radius, 0, Dp(1), color);
            _translation.SetShadowLayer(radius, 0, Dp(1), color);
        }
        else
        {
            // 半径 0 = 关掉阴影。
            _original.SetShadowLayer(0, 0, 0, Color.Transparent);
            _translation.SetShadowLayer(0, 0, 0, Color.Transparent);
        }
    }

    private static Color ToAndroidColor(Avalonia.Media.Color color)
        => Color.Argb(color.A, color.R, color.G, color.B);

    /// <summary>
    /// 按两行文字的实际宽度设置窗口宽度（显式像素值，不是 WrapContent）。
    /// 原因：WrapContent 的悬浮窗会被部分 OEM（实测一加 7 Pro）钳到 320dp，
    /// 长歌词因此被逼着换行，看起来像"明明一行放得下却换行"。
    /// 显式宽度不受该限制，窗口宽度 = min(文字宽 + 内边距, 屏宽/480dp)。
    /// </summary>
    private void FitWindowWidth()
    {
        if (_layout is null) return;
        var metrics = _context.Resources!.DisplayMetrics!;
        var maxWindow = Math.Min(Dp(480), metrics.WidthPixels);
        var textWidth = Math.Max(MeasureText(_original), MeasureText(_translation));
        var width = (int)Math.Ceiling(textWidth) + Dp(24); // 左右 padding 各 12dp
        _layout.Width = Math.Clamp(width, Dp(120), maxWindow);
    }

    private static float MeasureText(TextView? view)
        => view?.Text is { Length: > 0 } text && view.Paint is { } paint
            ? paint.MeasureText(text) : 0f;

    private void CreateWindow()
    {
        _manager = _context.GetSystemService(Context.WindowService)?.JavaCast<IWindowManager>()
            ?? throw new InvalidOperationException("WindowManager unavailable");
        _root = new LinearLayout(_context) { Orientation = Orientation.Vertical };
        _root.Touch += OnDrag;
        // 窗口只承载歌词本身。原来顶部那条 48dp 工具栏（A−/A+/译/背景/穿透/×）已移除：
        // 它把歌词顶死，导致窗口 Y 到 0 时歌词仍差 48dp 才贴顶；这些开关在设置页
        // 「桌面歌词」对话框里都有，触屏下长期占位并不划算。见 docs/android-notes.md。
        _root.SetPadding(Dp(12), Dp(8), Dp(12), Dp(8));
        // 宽度随内容走：短歌词窗口就窄，才拖得动；给个下限避免只剩一个字时抓不住。
        _root.SetMinimumWidth(Dp(120));
        var background = new GradientDrawable();
        background.SetColor(Color.Argb(220, 35, 39, 45));
        background.SetCornerRadius(Dp(12));
        _root.Background = background;
        var text = new LinearLayout(_context) { Orientation = Orientation.Vertical };
        // 原文与翻译两行字号不同(翻译 0.7 倍),宽度交给容器居中,两行才相对最宽行对齐。
        text.SetGravity(GravityFlags.CenterHorizontal);
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
        // 宽度用 MatchParent,由窗口宽度决定;窗口宽度再按文字实测宽度算(FitWindowWidth)。
        // ⚠ 不要回到 WrapContent:实测一加 7 Pro 会把 WrapContent 的悬浮窗钳到 320dp,
        // 长歌词因此被逼着换行("明明一行放得下")。显式像素宽度不受这个限制。
        text.AddView(_original, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        text.AddView(_translation, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        text.Touch += OnDrag;
        _original.Touch += OnDrag;
        _translation.Touch += OnDrag;
        _root.AddView(text, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        _layout = new WindowManagerLayoutParams(
            Dp(240), ViewGroup.LayoutParams.WrapContent,
            OperatingSystem.IsAndroidVersionAtLeast(26) ? WindowManagerTypes.ApplicationOverlay : WindowManagerTypes.Phone,
            WindowManagerFlags.NotFocusable | WindowManagerFlags.NotTouchModal,
            Format.Translucent)
        {
            Gravity = GravityFlags.Top | GravityFlags.Left,
            X = _state.AndroidLyricX ?? Dp(12),
            Y = _state.AndroidLyricY ?? Dp(100),
        };
        ClampPosition();
        // 换行会让窗口变高(宽度已由 FitWindowWidth 显式设定),贴底时 Y 会越界;
        // 布局完成后重钳一次。X/Y 没变就不 UpdateViewLayout,避免"布局→钳制→再布局"的死循环。
        _root.LayoutChange += (_, _) =>
        {
            if (_disposed || _layout is null || _root is null) return;
            var x = _layout.X;
            var y = _layout.Y;
            ClampPosition();
            if (x == _layout.X && y == _layout.Y) return;
            try { _manager!.UpdateViewLayout(_root, _layout); }
            catch (Exception ex) { Fail("悬浮歌词显示失败，请重新开启。", ex); }
        };
        _manager.AddView(_root, _layout);
    }

    private void ClampPosition()
    {
        if (_layout is null) return;
        var metrics = _context.Resources!.DisplayMetrics!;
        // 宽度由 FitWindowWidth 按文字实测宽度写入,这里只钳位置。
        // ⚠ 别再把它写死成"屏宽 − 16dp"(曾经如此) ⇒ X 的活动范围只剩 16dp,
        // 手感上就是"只能水平居中、左右拖不动"(见 docs/android-notes.md §13)。
        var width = _layout.Width > 0 ? _layout.Width : Dp(120);
        _layout.X = Math.Clamp(_layout.X, 0, Math.Max(0, metrics.WidthPixels - width));
        var height = _root?.Height > 0 ? _root.Height : Dp(100);
        // 下限 0：窗口顶就是歌词顶，可以贴到屏幕最上方（原先被 48dp 工具栏顶住，最多差一行）。
        // 底部留 48dp，避开手势条与导航栏。
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
                _state.AndroidLyricY = _layout.Y;
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
        _original = null;
        _translation = null;
        _layout = null;
    }

    public void Dispose()
    {
        _disposed = true;
        _model.PropertyChanged -= OnChanged;
        _handler.Post(Hide);
    }
}
