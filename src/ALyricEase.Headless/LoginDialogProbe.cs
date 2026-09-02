using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ALyricEase.Infrastructure;
using ALyricEase.ViewModels;
using ALyricEase.Views;

namespace ALyricEase.Headless;

internal static class LoginDialogProbe
{
    public static void Run()
    {
        var vm = ServiceLocator.Get<MainViewModel>();
        vm.Playlist.IsQQLoginTab = true;
        var view = new LoginDialogView { DataContext = vm };
        var window = new Window { Width = 1280, Height = 720, Content = view };
        window.Show();
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
