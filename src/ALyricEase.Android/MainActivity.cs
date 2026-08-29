using System;
using Android.App;
using Android.Content.PM;
using Android.OS;
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

    /// <summary>应用级返回回调,挂 AndroidX OnBackPressedDispatcher。
    /// Avalonia 的 AvaloniaActivity 走的就是这条链(OnStart 里 AddCallback,Android 13+ 再由
    /// AndroidX 桥接到平台 OnBackInvokedDispatcher):直接向系统注册平台回调会因注册时机早于
    /// AndroidX 的桥接被压在栈下,返回事件到不了应用层,表现即"按返回直接退到桌面"。
    /// AndroidX 调度器后注册者优先,base.OnStart() 之后再 AddCallback 即稳定排在 Avalonia 之前。</summary>
    private BackCallback? _backCallback;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        RequestNotificationPermission();
    }

    protected override void OnStart()
    {
        base.OnStart();

        // 排在 AvaloniaActivity 的回调之后加入 = 调度器栈顶,系统返回必先经过这里
        _backCallback = new BackCallback(this);
        OnBackPressedDispatcher.AddCallback(this, _backCallback);
    }

    protected override void OnStop()
    {
        _backCallback?.Remove();
        _backCallback = null;

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

    /// <summary>覆盖返回手势与三键导航(≤32 走 Activity.OnBackPressed 路由,13+ 经 AndroidX 桥接)。</summary>
    private sealed class BackCallback(MainActivity activity) : AndroidX.Activity.OnBackPressedCallback(true)
    {
        public override void HandleOnBackPressed()
        {
            global::Android.Util.Log.Debug(BackLogTag, "back via OnBackPressedDispatcher");
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
