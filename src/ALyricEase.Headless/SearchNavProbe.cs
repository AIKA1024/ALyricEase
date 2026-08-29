using System;
using System.Threading;
using Avalonia.Threading;
using ALyricEase.Infrastructure;
using ALyricEase.ViewModels;

namespace ALyricEase.Headless;

/// <summary>TEMP-DIAG:搜索页导航行为验证(真实搜索):
/// ① 结果页 ← 回登录页而非弹页面栈;② 菜单重进搜索页重置为登录页;③ 下钻(歌手页)← 保留结果。</summary>
public static class SearchNavProbe
{
    public static void Run()
    {
        var main = ServiceLocator.Get<MainViewModel>();
        var search = main.Search;
        var pass = true;

        // ① 结果页标题栏 ← → 登录页(ActivePage 仍是 Search)
        main.ActivePage = "Search";
        DoSearch(search, "AIKA");
        main.GoBackCommand.Execute(null);
        pass &= Check("① 结果页←回登录页", main.ActivePage == "Search" && !search.HasSearched);
        main.GoBackCommand.Execute(null); // 登录页 ← 才弹页面栈
        pass &= Check("①b 登录页←弹页面栈", main.ActivePage != "Search");

        // ② 菜单重进搜索页:重置为登录页(不残留结果)
        main.ActivePage = "Search";
        DoSearch(search, "AIKA");
        main.ActivePage = "Recommend";
        main.ActivePage = "Search";
        pass &= Check("② 菜单重进重置登录页", main.ActivePage == "Search" && !search.HasSearched);

        // ③ 结果页下钻(进歌手页)← → 回到搜索结果(上下文保留)
        DoSearch(search, "AIKA");
        main.ActivePage = "Artist"; // 真实路径 OpenSearchArtist 同样置 ActivePage=Artist
        main.GoBackCommand.Execute(null);
        pass &= Check("③ 下钻←保留结果", main.ActivePage == "Search" && search.HasSearched);

        Console.WriteLine(pass ? "[sn] 全部通过" : "[sn] 存在失败项");
        Environment.ExitCode = pass ? 0 : 1;
    }

    private static void DoSearch(SearchViewModel search, string keyword)
    {
        search.Keyword = keyword;
        search.SearchCommand.Execute(null);
        for (var i = 0; i < 300 && search.IsSearching; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(100);
            Dispatcher.UIThread.RunJobs();
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static bool Check(string name, bool ok)
    {
        Console.WriteLine($"[sn] {name}: {(ok ? "通过" : "失败")}");
        return ok;
    }
}
