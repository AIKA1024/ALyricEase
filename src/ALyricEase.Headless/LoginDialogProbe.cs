using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ALyricEase.Infrastructure;
using ALyricEase.ViewModels;
using ALyricEase.Views;
using Net.Codecrete.QrCodeGenerator;

namespace ALyricEase.Headless;

internal static class LoginDialogProbe
{
    public static void Run()
    {
        var vm = ServiceLocator.Get<MainViewModel>();
        vm.Playlist.IsQQLoginTab = false;
        var view = new LoginDialogView { DataContext = vm };
        var window = new Window { Width = 1280, Height = 720, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Render(window, "ne-qr");
        // 本地生成一张二维码填充态,验证图片绑定与 214×214 卡片布局(不触网)。
        var probePng = QrCode.EncodeText(
            "https://music.163.com/login?codekey=00000000-0000-0000-0000-000000000000",
            QrCode.Ecc.Medium).ToPngBitmap(border: 3, scale: 8);
        using (var stream = new MemoryStream(probePng, writable: false))
            vm.Playlist.NetEaseQrImage = new Bitmap(stream);
        Dispatcher.UIThread.RunJobs();
        Render(window, "ne-qr-filled");
        vm.Playlist.SelectNetEasePhoneMethodCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Render(window, "ne-phone");
        vm.Playlist.SelectNetEaseCookieMethodCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Render(window, "ne-cookie");

        vm.Playlist.IsQQLoginTab = true;
        vm.Playlist.SelectQqQrLoginMethodCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Render(window, "qr");
        vm.Playlist.SelectQqPhoneLoginMethodCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Render(window, "phone");
        vm.Playlist.SelectQqCookieLoginMethodCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Render(window, "cookie");
        window.Width = 600;
        window.Height = 600;
        vm.Playlist.SelectQqPhoneLoginMethodCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Render(window, "phone-compact");

        vm.Playlist.IsQQLoginTab = false;
        vm.Playlist.SelectNetEasePhoneMethodCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Render(window, "ne-phone-compact");
    }

    private static void Render(Window window, string state)
    {
        var size = new PixelSize((int)window.ClientSize.Width, (int)window.ClientSize.Height);
        using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
        bitmap.Render(window);
        var path = Path.Combine(Path.GetTempPath(), $"aly-login-{state}.png");
        bitmap.Save(path, PngBitmapEncoderOptions.Default);
        Console.WriteLine($"[login:{state}] {path}");
    }
}
