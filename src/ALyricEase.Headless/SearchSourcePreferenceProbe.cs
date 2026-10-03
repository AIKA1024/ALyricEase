using ALyricEase.Infrastructure;
using ALyricEase.Services;
using ALyricEase.ViewModels;
using ALyricEase.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;

namespace ALyricEase.Headless;

/// <summary>搜索来源持久化和 Fluent 2 分段选择器渲染回归；不触网。</summary>
public static class SearchSourcePreferenceProbe
{
    public static void Run()
    {
        var stateDirectory = Path.Combine(Path.GetTempPath(), $"alyric-search-source-{Guid.NewGuid():N}");
        var statePath = Path.Combine(stateDirectory, "state.json");

        try
        {
            var sources = ServiceLocator.Get<MusicApiProvider>();
            var player = ServiceLocator.Get<PlayerViewModel>();
            var state = new AppStateStore(statePath);
            var vm = new SearchViewModel(sources, player, state);

            Assert(vm.IsCombinedSource, "首次启动默认选择综合");
            Render(vm, "search_source_combined.png");

            vm.SelectNetEaseSourceCommand.Execute(null);
            Assert(vm.IsNetEaseSource && state.PreferredSearchSource == SearchSourceMode.NetEase,
                "选择网易云后立即保存");
            Render(vm, "search_source_netease.png");

            vm.SelectQQSourceCommand.Execute(null);
            Assert(vm.IsQQSource && state.PreferredSearchSource == SearchSourceMode.QQ,
                "选择 QQ 音乐后立即保存");
            Render(vm, "search_source_qq.png");

            var restoredState = new AppStateStore(statePath);
            var restoredVm = new SearchViewModel(sources, player, restoredState);
            Assert(restoredVm.IsQQSource, "重新启动后恢复上次选择的 QQ 音乐");
        }
        finally
        {
            if (Directory.Exists(stateDirectory))
                Directory.Delete(stateDirectory, recursive: true);
        }
    }

    private static void Render(SearchViewModel vm, string fileName)
    {
        var window = new Window
        {
            Width = 517,
            Height = 800,
            RequestedThemeVariant = ThemeVariant.Dark,
            Content = new SearchView { DataContext = vm },
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Thread.Sleep(200);
        Dispatcher.UIThread.RunJobs();

        var pixelSize = new PixelSize((int)window.ClientSize.Width, (int)window.ClientSize.Height);
        using var bitmap = new RenderTargetBitmap(pixelSize, new Vector(96, 96));
        bitmap.Render(window);
        var outputDirectory = Path.Combine(Directory.GetCurrentDirectory(), "tmpandroid");
        Directory.CreateDirectory(outputDirectory);
        var path = Path.Combine(outputDirectory, fileName);
        bitmap.Save(path, PngBitmapEncoderOptions.Default);
        Console.WriteLine($"[searchsource] 截图: {path}");
        window.Close();
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException($"[searchsource] FAIL {message}");

        Console.WriteLine($"[searchsource] PASS {message}");
    }
}
