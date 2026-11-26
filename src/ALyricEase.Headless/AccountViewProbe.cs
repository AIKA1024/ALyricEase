using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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

        ReportSettingsGeometry(window);
    }

    /// <summary>换主题后的两处几何回归:①开关要贴卡片右缘(别被模板的 MinWidth 撑开) ②Expander 展开后容器宽度必须等于 header。</summary>
    private static void ReportSettingsGeometry(Window window)
    {
        foreach (var card in window.GetVisualDescendants().OfType<Border>()
                     .Where(b => b.Classes.Contains("setting-card")))
        {
            var toggle = card.GetVisualDescendants().OfType<ToggleSwitch>().FirstOrDefault();
            if (toggle is null) continue;
            var switchArea = toggle.GetVisualDescendants().OfType<Border>()
                .FirstOrDefault(b => b.Name == "SwitchAreaBorder");
            if (switchArea is null)
            {
                Console.WriteLine("[geom:toggle] 找不到 SwitchAreaBorder");
                continue;
            }

            var cardRight = card.TranslatePoint(new Point(card.Bounds.Width, 0), window)!.Value.X;
            var toggleRight = toggle.TranslatePoint(new Point(toggle.Bounds.Width, 0), window)!.Value.X;
            var switchRight = switchArea.TranslatePoint(new Point(switchArea.Bounds.Width, 0), window)!.Value.X;
            var switchLeft = switchArea.TranslatePoint(new Point(0, 0), window)!.Value.X;
            Console.WriteLine($"[geom:toggle] 卡片右缘={cardRight:F1} 控件宽={toggle.Bounds.Width:F1} " +
                $"控件右缘={toggleRight:F1} 可见开关=[{switchLeft:F1},{switchRight:F1}] " +
                $"可见右缘距卡片={cardRight - switchRight:F1}px");
        }

        foreach (var expander in window.GetVisualDescendants().OfType<Expander>()
                     .Where(e => e.Classes.Contains("setting-expander")))
        {
            expander.IsExpanded = true;
            Dispatcher.UIThread.RunJobs();

            var header = expander.GetVisualDescendants().OfType<ToggleButton>()
                .FirstOrDefault(t => t.Name == "ExpanderHeader");
            var content = expander.GetVisualDescendants().OfType<Border>()
                .FirstOrDefault(b => b.Name == "ExpanderContent");
            if (header is null || content is null) { Console.WriteLine("[geom:expander] 找不到 header/content"); continue; }

            // 行头真正被画出来的底色是模板里的 Border#ToggleButtonBackground;它的边界才是眼睛看到的宽度
            var headerFill = header.GetVisualDescendants().OfType<Border>()
                .FirstOrDefault(b => b.Name == "ToggleButtonBackground");
            var hx = header.TranslatePoint(new Point(0, 0), window)!.Value.X;
            var cx = content.TranslatePoint(new Point(0, 0), window)!.Value.X;
            var fx = headerFill?.TranslatePoint(new Point(0, 0), window)!.Value.X;
            var fw = headerFill?.Bounds.Width;
            var bt = content.BorderThickness;
            var divider = (bt.Top, bt.Left, bt.Right, bt.Bottom, content.BorderBrush?.ToString() ?? "null");
            Console.WriteLine($"[geom:expander] header=[{hx:F1},{(hx + header.Bounds.Width):F1}] " +
                $"content=[{cx:F1},{(cx + content.Bounds.Width):F1}] " +
                $"左差={cx - hx:F1} 右差={(cx + content.Bounds.Width) - (hx + header.Bounds.Width):F1}" +
                (headerFill is null
                    ? " | 找不到 ToggleButtonBackground"
                    : $" | 行头底色=[{fx:F1},{fx + fw:F1}] 与内容左差={cx - fx!.Value:F1} 右差={(cx + content.Bounds.Width) - (fx.Value + fw!.Value):F1}") +
                $" | 内容边框 上{bt.Top}/左{bt.Left}/右{bt.Right}/下{bt.Bottom} 画刷={divider.Item5}");
        }

        Dispatcher.UIThread.RunJobs();
        // 滚到底,让"详细信息"展开态进画面(否则截图只有首屏)
        var scroller = window.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (scroller is not null)
        {
            scroller.Offset = new Vector(0, scroller.Extent.Height);
            Dispatcher.UIThread.RunJobs();
        }
        var size = new PixelSize((int)window.ClientSize.Width, (int)window.ClientSize.Height);
        using var shot = new RenderTargetBitmap(size, new Vector(96, 96));
        shot.Render(window);
        var expanderPath = Path.Combine(Path.GetTempPath(), "aly-settings-expanded.png");
        shot.Save(expanderPath, PngBitmapEncoderOptions.Default);
        Console.WriteLine($"[settings:expanded] {expanderPath}");

        // 深色再出一张:用户实际用的就是暗色,亮色通过不代表暗色没问题
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        Dispatcher.UIThread.RunJobs();
        var darkSize = new PixelSize((int)window.ClientSize.Width, (int)window.ClientSize.Height);
        using var darkShot = new RenderTargetBitmap(darkSize, new Vector(96, 96));
        darkShot.Render(window);
        var darkPath = Path.Combine(Path.GetTempPath(), "aly-settings-expanded-dark.png");
        darkShot.Save(darkPath, PngBitmapEncoderOptions.Default);
        Console.WriteLine($"[settings:expanded-dark] {darkPath}");
        Application.Current.RequestedThemeVariant = ThemeVariant.Light;
        Dispatcher.UIThread.RunJobs();
    }
}
