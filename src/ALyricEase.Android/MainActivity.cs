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

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        // 系统返回的唯一入口,手势返回与三大金刚键返回键都汇聚到这里:
        // API 33+ 由 Avalonia 注册的 OnBackPressedCallback 转发、以下由 OnBackPressed 覆写转发,
        // 两条路都最终触发 AvaloniaActivity.BackRequested(基类的 OnBackInvoked)。
        BackRequested += OnBackRequested;
        RequestNotificationPermission();
    }

    /// <summary>系统返回:先让共享 VM 逐级消费一层,消费不掉再退到后台。</summary>
    private void OnBackRequested(object? sender, AndroidBackRequestedEventArgs e)
    {
        if (ServiceLocator.Get<MainViewModel>().TryHandleBack())
        {
            // 已消费(关弹窗 / 收覆盖层 / 关抽屉 / 页面返回):阻止系统默认返回
            e.Handled = true;
            return;
        }

        // 已在根页面:不结束 Activity —— 音频由本进程内的 AndroidMediaPlayer 播放,
        // finish 会直接中断播放。退到后台(等价按 Home),前台媒体通知与播放继续。
        MoveTaskToBack(true);
        e.Handled = true;
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
