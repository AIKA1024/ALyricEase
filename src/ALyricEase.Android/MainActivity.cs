using System;
using Android.App;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using AndroidX.Core.Content;
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
        RequestNotificationPermission();
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
