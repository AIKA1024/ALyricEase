using System;
using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Window;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using ALyricEase.Infrastructure;
using ALyricEase.ViewModels;
using Avalonia.Android;

namespace ALyricEase;

[Activity(
    Label = "ALyricEase",
    MainLauncher = true,
    Theme = "@style/MyTheme.NoActionBar",
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize |
                           ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize |
                           ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity
{
    private const int NotificationPermissionRequestCode = 2001;

    /// <summary>返回相关日志标签(adb logcat -s ALyricEaseBack,D 可看返回有没有到应用层)。</summary>
    private const string BackLogTag = "ALyricEaseBack";

    // Android 13+ 不再保证把返回事件转换成 Activity.OnBackPressed。直接持有平台回调，
    // 同时也避免依赖 Avalonia/AndroidX 在不同厂商 ROM 上的桥接实现。
    private IOnBackInvokedCallback? _platformBackCallback;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        RequestNotificationPermission();
    }

    protected override void OnStart()
    {
        base.OnStart();

        if (OperatingSystem.IsAndroidVersionAtLeast(33) && _platformBackCallback is null)
        {
            // Avalonia 会在 base.OnStart 中注册 AndroidX 返回回调，所以平台回调必须随后注册。
            // 同优先级按注册逆序调用，这样系统稳定地先进入本应用的导航处理。
            _platformBackCallback = new PlatformBackCallback(this);
            OnBackInvokedDispatcher.RegisterOnBackInvokedCallback(
                IOnBackInvokedDispatcher.PriorityDefault,
                _platformBackCallback);
        }
    }

    /// <summary>Android 12 及以下的返回入口。Android 13+ 使用 OnBackInvokedDispatcher。</summary>
#pragma warning disable CS0618 // API 32 及以下仍需覆写 Activity.OnBackPressed
    public override void OnBackPressed()
    {
        global::Android.Util.Log.Debug(BackLogTag, "back via OnBackPressed");
        HandleSystemBack();
    }
#pragma warning restore CS0618

    protected override void OnStop()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(33) && _platformBackCallback is not null)
        {
            OnBackInvokedDispatcher.UnregisterOnBackInvokedCallback(_platformBackCallback);
            _platformBackCallback.Dispose();
            _platformBackCallback = null;
        }

        base.OnStop();
    }

    /// <summary>系统返回:先让共享 VM 逐级消费一层,消费不掉再退到后台。</summary>
    private void HandleSystemBack()
    {
        if (ServiceLocator.Get<MainViewModel>().TryHandleBack())
            return; // 已消费(关弹窗 / 收覆盖层 / 关抽屉 / 页面返回)

        // 已在根页面:不结束 Activity —— 音频由本进程内的 AndroidMediaPlayer 播放,
        // finish 会直接中断播放。退到后台(等价按 Home),前台媒体通知与播放继续。
        MoveTaskToBack(true);
    }

    /// <summary>Android 13+ 原生返回回调，覆盖返回手势与三键导航的返回键。</summary>
    private sealed class PlatformBackCallback(MainActivity activity) :
        Java.Lang.Object, IOnBackInvokedCallback
    {
        public void OnBackInvoked()
        {
            global::Android.Util.Log.Debug(BackLogTag, "back via OnBackInvokedDispatcher");
            activity.HandleSystemBack();
        }
    }

    private void RequestNotificationPermission()
    {
        // Android 13+ (API 33) 的通知运行时权限。媒体会话通知本身豁免该权限,
        // 但申请后应用可正常发布其他类型通知,并兼容部分厂商 ROM 的实现差异。
        if (!OperatingSystem.IsAndroidVersionAtLeast(33)) return;
        if (ContextCompat.CheckSelfPermission(this, global::Android.Manifest.Permission.PostNotifications)
            != Permission.Granted)
        {
            ActivityCompat.RequestPermissions(
                this,
                new[] { global::Android.Manifest.Permission.PostNotifications },
                NotificationPermissionRequestCode);
        }
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        // 无论结果如何应用都能正常运行;媒体横幅靠媒体会话显示,不依赖该权限
    }
}
