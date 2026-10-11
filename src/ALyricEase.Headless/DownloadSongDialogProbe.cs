using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.ViewModels;
using ALyricEase.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ALyricEase.Headless;

internal static class DownloadSongDialogProbe
{
    public static void Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var main = ServiceLocator.Get<MainViewModel>();
        var song = new Song { Id = 1, Name = "后来", Artist = "刘若英" };
        var online = SongContextMenu.Create(new Border(), song, null);
        var local = SongContextMenu.Create(new Border(), new Song { Source = MusicSource.Local }, null);
        if (!online.Items.OfType<MenuItem>().Any(x => Equals(x.Header, "下载"))
            || local.Items.OfType<MenuItem>().Any(x => Equals(x.Header, "下载")))
            throw new InvalidOperationException("下载菜单的音源可见性错误");
        foreach (var (width, height, theme) in new[] {
            (820, 740, ThemeVariant.Light), (390, 700, ThemeVariant.Dark), (320, 480, ThemeVariant.Light) })
        {
            main.IsDownloadSongDialogOpen = false;
            main.OpenDownloadSongDialogCommand.Execute(song);
            var view = new DownloadSongDialogView { DataContext = main, IsVisible = false };
            var behind = new Button { Content = "背景按钮" };
            var window = new Window { Width = width, Height = height,
                RequestedThemeVariant = theme, Content = new Grid { Children = { behind, view } } };
            window.Show();
            behind.Focus();
            view.IsVisible = true;
            Dispatcher.UIThread.RunJobs();
            var card = view.FindControl<Border>("DialogCard")!;
            var bounds = card.Bounds;
            if (bounds.Width > width - 30 || bounds.Height > height - 30)
                throw new InvalidOperationException($"Dialog overflow {width}x{height}: {bounds}");
            for (var i = 0; i < 12; i++)
            {
                window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
                Dispatcher.UIThread.RunJobs();
                if (window.FocusManager?.GetFocusedElement() is not Visual focus || !view.IsVisualAncestorOf(focus))
                    throw new InvalidOperationException($"Tab 焦点逃出模态窗口: {width}, step={i}, focus={window.FocusManager?.GetFocusedElement()}");
            }
            using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
            bitmap.Render(window);
            bitmap.Save(Path.Combine(directory, $"download-{width}-{theme}.png"), PngBitmapEncoderOptions.Default);
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            if (main.IsDownloadSongDialogOpen) throw new InvalidOperationException("Esc 未关闭模态窗口");
            window.Close();
        }
        main.OpenDownloadSongDialogCommand.Execute(song);
        if (!main.TryHandleBack() || main.IsDownloadSongDialogOpen)
            throw new InvalidOperationException("系统返回未关闭下载窗口");
        Console.WriteLine("PASS: online/local menus, desktop/mobile/short-screen layouts, light/dark, Tab trap, Escape, system back.");
    }
}
