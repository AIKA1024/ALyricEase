using System.ComponentModel;
using ALyricEase.Infrastructure;
using ALyricEase.ViewModels;

namespace ALyricEase.Headless;

/// <summary>顶部横幅标题回归(<c>--shelltitle</c>):歌单详情页必须以**页面自己的歌单名**为准。
///
/// 复现路径(用户报障):从个性推荐卡片/搜索/用户主页进入歌单不经过侧栏,而这些详情页的
/// <c>ActivePage</c> 统一是 "Favorites";<c>OnActivePageChanged</c> 会把 SelectedNav 自动
/// 切到"我喜欢的音乐"导航项 ⇒ 旧实现取 <c>SelectedNav.Label</c> 当标题,横幅就显示
/// "我喜欢的音乐"。侧栏点歌单子项时 SelectedNav 本身就是那个子项,所以只有非侧栏入口能复现。
/// 加载是异步的(ActivePage 先切、歌单名晚一拍),标题还必须随 PlaylistTitle 变化重新通知。</summary>
public static class ShellTitleProbe
{
    public static int Run()
    {
        var vm = ServiceLocator.Get<MainViewModel>();
        var exit = 0;

        // 1) 非侧栏入口打开歌单:ActivePage 已是 Favorites,随后歌单名到位
        vm.ActivePage = "Favorites";
        var titleNotifications = 0;
        void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.CurrentPageTitle)) titleNotifications++;
        }

        vm.PropertyChanged += OnViewModelChanged;
        try
        {
            vm.Playlist.PlaylistTitle = "推荐里的歌单";
            exit |= Check("推荐入口打开歌单", vm.CurrentPageTitle, "推荐里的歌单");
            exit |= titleNotifications > 0
                ? Pass("标题随歌单名变化重新通知")
                : Fail("标题没有随歌单名变化重新通知");
        }
        finally
        {
            vm.PropertyChanged -= OnViewModelChanged;
        }

        // 2) "我喜欢的音乐"自己打开时仍然是它自己的名字
        vm.Playlist.PlaylistTitle = "我喜欢的音乐";
        exit |= Check("收藏页", vm.CurrentPageTitle, "我喜欢的音乐");

        // 3) 非歌单页沿用导航项/页面键的文案
        vm.Playlist.PlaylistTitle = "";
        vm.ActivePage = "Recommend";
        exit |= Check("个性推荐页", vm.CurrentPageTitle, "个性推荐");

        Console.WriteLine(exit == 0 ? "[shelltitle] PASS" : "[shelltitle] FAIL");
        return exit;
    }

    private static int Check(string stage, string actual, string expected)
    {
        var ok = string.Equals(actual, expected, StringComparison.Ordinal);
        Console.WriteLine($"[shelltitle] {stage}: 标题=“{actual}” 期望=“{expected}” → {(ok ? "OK" : "FAIL")}");
        return ok ? 0 : 1;
    }

    private static int Pass(string message)
    {
        Console.WriteLine($"[shelltitle] {message} → OK");
        return 0;
    }

    private static int Fail(string message)
    {
        Console.WriteLine($"[shelltitle] {message} → FAIL");
        return 1;
    }
}
