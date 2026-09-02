using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ALyricEase.Infrastructure;
using ALyricEase.ViewModels;
using ALyricEase.Views;

namespace ALyricEase.Headless;

internal static class AccountViewProbe
{
    public static void Run()
    {
        var vm = ServiceLocator.Get<AccountViewModel>();
        Fill(vm.NetEase, "晚风与海", "328401927", "黑胶 VIP", "LV 7", "LV 10", "有效期至 2027-08-31");
        Fill(vm.Qq, "Aika", "194728631", "豪华绿钻", "LV 6", "LV 12", "有效期至 2027-03-18");

        var window = new Window
        {
            Width = 1100,
            Height = 760,
            Content = new AccountView { DataContext = vm },
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Render(window, "wide");

        var logout = window.GetVisualDescendants().OfType<Button>()
            .First(button => Equals(button.Content, "退出网易云音乐"));
        var hoverPoint = logout.TranslatePoint(
            new Point(logout.Bounds.Width / 2, logout.Bounds.Height / 2), window)!.Value;
        window.MouseMove(hoverPoint);
        Dispatcher.UIThread.RunJobs();
        Render(window, "hover-light");

        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        Dispatcher.UIThread.RunJobs();
        Render(window, "hover-dark");

        Application.Current.RequestedThemeVariant = ThemeVariant.Light;
        window.MouseMove(new Point(0, 0));
        window.Width = 600;
        window.Height = 700;
        Dispatcher.UIThread.RunJobs();
        Render(window, "compact");

        window.Width = 900;
        window.Height = 760;
        window.Content = new SettingsView { DataContext = ServiceLocator.Get<SettingsViewModel>() };
        Dispatcher.UIThread.RunJobs();
        RenderSettings(window);
    }

    private static void Fill(AccountPlatformViewModel account, string name, string id,
        string membership, string vipLevel, string accountLevel, string detail)
    {
        account.IsLoggedIn = true;
        account.Username = name;
        account.UserIdText = id;
        account.MembershipName = membership;
        account.MembershipLevelText = vipLevel;
        account.AccountLevelText = accountLevel;
        account.MembershipDetail = detail;
    }

    private static void Render(Window window, string state)
    {
        var size = new PixelSize((int)window.ClientSize.Width, (int)window.ClientSize.Height);
        using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
        bitmap.Render(window);
        var path = Path.Combine(Path.GetTempPath(), $"aly-account-{state}.png");
        bitmap.Save(path, PngBitmapEncoderOptions.Default);
        Console.WriteLine($"[account:{state}] {path}");
    }

    private static void RenderSettings(Window window)
    {
        var size = new PixelSize((int)window.ClientSize.Width, (int)window.ClientSize.Height);
        using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
        bitmap.Render(window);
        var path = Path.Combine(Path.GetTempPath(), "aly-settings-buttons.png");
        bitmap.Save(path, PngBitmapEncoderOptions.Default);
        Console.WriteLine($"[settings:buttons] {path}");
    }
}
