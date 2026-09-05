using ALyricEase.Services;
using Android.Content;

namespace ALyricEase.Services.Sharing;

/// <summary>Android 系统分享选择器。</summary>
public sealed class AndroidShareService : IPlatformShareService
{
    public Task<bool> ShareUriAsync(nint ownerHandle, string title, string description, Uri uri)
    {
        try
        {
            var send = new Intent(Intent.ActionSend);
            send.SetType("text/plain");
            send.PutExtra(Intent.ExtraSubject, title);
            send.PutExtra(Intent.ExtraText, $"{description}{Environment.NewLine}{uri}");

            var chooser = Intent.CreateChooser(send, "分享");
            if (chooser is null) return Task.FromResult(false);
            chooser.AddFlags(ActivityFlags.NewTask);
            global::Android.App.Application.Context.StartActivity(chooser);
            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }
}
