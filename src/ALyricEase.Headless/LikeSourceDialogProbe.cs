using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.ViewModels;
using ALyricEase.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace ALyricEase.Headless;

/// <summary>综合搜索红心平台 Fluent 2 对话框渲染探针（桌面与窄屏），不触网。</summary>
public static class LikeSourceDialogProbe
{
    public static void Run()
    {
        var netEase = new Song
        {
            Source = MusicSource.NetEase, Id = 1, Name = "后来", Artist = "刘若英",
            ArtistNames = ["刘若英"], Album = "我等你", DurationMs = 341_000,
        };
        var qq = new Song
        {
            Source = MusicSource.QQ, Id = 2, Mid = "probe-mid", Name = "后来", Artist = "刘若英",
            ArtistNames = ["刘若英"], Album = "我等你", DurationMs = 341_500,
        };
        var merged = SongSearchMerger.Merge([netEase], [qq], 1)[0];
        var row = new SongItemViewModel(merged, _ => Task.FromResult(true));
        var main = ServiceLocator.Get<MainViewModel>();
        main.OpenLikeSourceDialog(row);

        Render(main, "like_source_dialog_desktop.png", 820, 640);
        Render(main, "like_source_dialog_mobile.png", 390, 700);
    }

    private static void Render(MainViewModel main, string fileName, double width, double height)
    {
        var view = new LikeSourceDialogView { DataContext = main };
        var window = new Window
        {
            Width = width,
            Height = height,
            Content = view,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();

        var pixelSize = new PixelSize((int)window.ClientSize.Width, (int)window.ClientSize.Height);
        using var bitmap = new RenderTargetBitmap(pixelSize, new Vector(96, 96));
        bitmap.Render(window);
        var outputDirectory = Path.Combine(Directory.GetCurrentDirectory(), "tmpandroid");
        Directory.CreateDirectory(outputDirectory);
        var path = Path.Combine(outputDirectory, fileName);
        bitmap.Save(path);
        Console.WriteLine($"[likesourcedlg] 截图: {path}");

        var selectedChoice = view.FindControl<Button>("NetEaseChoice");
        var hoverPoint = selectedChoice?.TranslatePoint(
            new Point(selectedChoice.Bounds.Width / 2, selectedChoice.Bounds.Height / 2), window);
        if (hoverPoint is { } point)
        {
            window.MouseMove(point);
            Dispatcher.UIThread.RunJobs();
            using var hoverBitmap = new RenderTargetBitmap(pixelSize, new Vector(96, 96));
            hoverBitmap.Render(window);
            var hoverPath = Path.Combine(
                outputDirectory,
                Path.GetFileNameWithoutExtension(fileName) + "_hover.png");
            hoverBitmap.Save(hoverPath);
            Console.WriteLine($"[likesourcedlg] Hover 截图: {hoverPath}");
        }
        window.Close();
    }
}
