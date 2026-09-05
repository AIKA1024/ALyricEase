using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ALyricEase.Infrastructure;
using ALyricEase.ViewModels;
using ALyricEase.Views;

namespace ALyricEase.Headless;

/// <summary>
/// 账号页登录态矩阵探针(--accountstates):未登录的平台应显示占位卡片并带该平台的登录按钮,
/// 已登录的显示真实账号卡片。验证:
/// 1) 三种登录态下"账号卡片总数恒为 2",且登录/未登录卡片互斥;
/// 2) 占位卡片上的"登录 X"按钮绑定到 LoginNetEase/LoginQq 命令,执行后弹层打开且标签定位到该平台。
/// 不触网(仅切换 VM 登录态、读布局)。
/// </summary>
public static class AccountStatesProbe
{
    public static void Run()
    {
        var vm = ServiceLocator.Get<AccountViewModel>();
        var main = ServiceLocator.Get<MainViewModel>();
        var playlist = ServiceLocator.Get<PlaylistViewModel>();

        var win = new Window { Width = 1100, Height = 820, Content = new AccountView { DataContext = vm } };
        win.Show();
        // 关键:登录态必须经 PlaylistViewModel 驱动(AccountViewModel.SyncLoginState 的数据源)。
        // 直接改 AccountPlatformViewModel.IsLoggedIn 会被真实登录态覆盖 —— Playlist 在构造时
        // 就从本机 Cookie 恢复了登录,并在属性变化时经 MainViewModel.OnPlaylistLoginChanged 回灌。
        // 注意不要动 MainViewModel.ActivePage:置为 Account 时登录态变 true 会触发 RefreshAsync 触网。
        playlist.UserName = "晚风与海";
        playlist.QqUserName = "Aika";
        Drain();

        State(win, vm, playlist, "均未登录", ne: false, qq: false);
        Shot(win, "none");
        State(win, vm, playlist, "仅网易云登录", ne: true, qq: false);
        Shot(win, "netease-only");
        State(win, vm, playlist, "仅 QQ 登录", ne: false, qq: true);
        State(win, vm, playlist, "两者均登录", ne: true, qq: true);

        // 单平台登录态下点未登录那张卡片的登录按钮:应打开登录弹层并定位到对应标签
        State(win, vm, playlist, "点击验证(仅网易云登录)", ne: true, qq: false);
        ClickLogin(win, main, playlist, "登录 QQ 音乐", expectQqTab: true);

        State(win, vm, playlist, "点击验证(仅 QQ 登录)", ne: false, qq: true);
        ClickLogin(win, main, playlist, "登录网易云音乐", expectQqTab: false);

        vm.NetEase.SetLoginState(false);
        vm.Qq.SetLoginState(false);
        if (main.IsLoginDialogOpen) main.CloseLoginDialogCommand.Execute(null);
        win.Close();
    }

    private static void State(Window win, AccountViewModel vm, PlaylistViewModel playlist,
        string tag, bool ne, bool qq)
    {
        // 经 Playlist 置位(真实路径);SyncLoginState 由属性变化自动触发,这里显式调一次保证同步
        playlist.IsLoggedIn = ne;
        playlist.IsQqLoggedIn = qq;
        vm.SyncLoginState();
        Drain();

        Console.WriteLine($"[acct:{tag}] 设置后 VM 状态: NetEase.IsLoggedIn={vm.NetEase.IsLoggedIn} (期望 {ne}) " +
            $"Qq.IsLoggedIn={vm.Qq.IsLoggedIn} (期望 {qq})");

        // 注意:父级隐藏时子元素自身 IsVisible 仍为 true,必须用 IsEffectivelyVisible 判断是否真的在可见树里
        var cards = win.GetVisualDescendants().OfType<Border>()
            .Where(b => b.Classes.Contains("account-card") && b.IsEffectivelyVisible)
            .ToList();
        var logins = cards.Select(CardLoginButton).Where(b => b is not null).ToList();
        var logouts = cards.SelectMany(c => c.GetVisualDescendants().OfType<Button>()
                .Where(b => b.Classes.Contains("logout") && b.IsEffectivelyVisible))
            .ToList();

        Console.WriteLine($"[acct:{tag}] 可见卡片={cards.Count} (期望 2) " +
            $"登录按钮=[{string.Join(", ", logins.Select(b => b!.Content))}] " +
            $"退出按钮=[{string.Join(", ", logouts.Select(b => b.Content))}]");
        for (var i = 0; i < cards.Count; i++)
        {
            var buttons = cards[i].GetVisualDescendants().OfType<Button>()
                .Where(b => b.IsEffectivelyVisible)
                .Select(b => $"{b.Content}/{string.Join("+", b.Classes)}")
                .ToList();
            Console.WriteLine($"    卡片{i}: 按钮=[{string.Join(" | ", buttons)}]");
        }
    }

    /// <summary>出图供人工核对占位卡片观感(未登录平台与已登录平台同屏时的对齐/留白)。</summary>
    private static void Shot(Window win, string name)
    {
        var size = new PixelSize((int)win.ClientSize.Width, (int)win.ClientSize.Height);
        using var bmp = new RenderTargetBitmap(size, new Vector(96, 96));
        bmp.Render(win);
        var path = Path.Combine(Path.GetTempPath(), $"aly-account-states-{name}.png");
        bmp.Save(path, PngBitmapEncoderOptions.Default);
        Console.WriteLine($"[acct:shot] {name} → {path}");
    }

    /// <summary>点指定文本的按钮,验证弹层打开且标签定位正确。</summary>
    private static void ClickLogin(Window win, MainViewModel main, PlaylistViewModel playlist,
        string buttonText, bool expectQqTab)
    {
        var button = win.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(b => b.IsVisible && Equals(b.Content, buttonText));
        if (button is null)
        {
            Console.WriteLine($"[acct:click] 找不到按钮 '{buttonText}'");
            return;
        }
        main.CloseLoginDialogCommand.Execute(null);
        playlist.IsQQLoginTab = expectQqTab; // 先置反,才能证明是命令切过去的
        Drain();
        button.Command?.Execute(button.CommandParameter);
        Drain();
        Console.WriteLine($"[acct:click] '{buttonText}' → 弹层打开={main.IsLoginDialogOpen} (期望 True) " +
            $"QQ 标签={playlist.IsQQLoginTab} (期望 {expectQqTab})");
        main.CloseLoginDialogCommand.Execute(null);
        Drain();
    }

    private static Button? CardLoginButton(Border card) =>
        card.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(b => b.IsEffectivelyVisible && b.Classes.Contains("account-primary"));

    private static void Drain()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
    }
}
