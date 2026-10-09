using System.Diagnostics;
using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.ViewModels;
using ALyricEase.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ALyricEase.Headless;

/// <summary>真实歌曲行模板：空封面显示默认图，复用/离树返回后仍可切换真实封面。</summary>
internal static class CoverFallbackProbe
{
    public static int Run()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aly-cover-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
        var panel = new StackPanel();
        var window = new Window { Width = 1000, Height = 300, Content = panel };
        try
        {
            window.Show();
            foreach (var theme in new[] { "TrackRowSearchTheme", "TrackRowPlaylistWideTheme" })
            {
                var row = new TrackRow
                {
                    Theme = (ControlTheme)Application.Current!.FindResource(theme)!,
                    DataContext = MakeRow("")
                };
                panel.Children.Add(row);
                Dispatcher.UIThread.RunJobs();
                var image = row.GetVisualDescendants().OfType<Image>()
                    .Single(i => ManagedCoverImage.GetFallbackSource(i) == CoverImageConverter.PlaceholderUri);
                WaitFor(() => image.Source is Bitmap b && b.PixelSize.Width > 1, theme + " initial placeholder");

                row.DataContext = MakeRow(path);
                WaitFor(() => image.Source is Bitmap b && b.PixelSize.Width == 1, theme + " real cover");
                row.DataContext = MakeRow("  ");
                WaitFor(() => image.Source is Bitmap b && b.PixelSize.Width > 1, theme + " recycled placeholder");
                panel.Children.Remove(row);
                Dispatcher.UIThread.RunJobs();
                panel.Children.Add(row);
                WaitFor(() => image.Source is Bitmap b && b.PixelSize.Width > 1, theme + " reattached placeholder");
                panel.Children.Remove(row);
            }

            var plainImage = new Image();
            ManagedCoverImage.SetDecodeSize(plainImage, 50);
            ManagedCoverImage.SetSource(plainImage, "");
            panel.Children.Add(plainImage);
            Dispatcher.UIThread.RunJobs();
            if (plainImage.Source is not null || AsyncImageLoader.ImageLoader.GetSource(plainImage) is not null)
                throw new InvalidOperationException("Images without an opt-in fallback must remain empty.");
            Console.WriteLine("[cover-fallback] PASS: both song row templates, real cover, recycling, reattach, opt-in isolation");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[cover-fallback] FAIL: {ex}");
            return 1;
        }
        finally
        {
            window.Close();
            File.Delete(path);
        }
    }

    private static SongItemViewModel MakeRow(string cover) => new(
        new Song { Source = MusicSource.Local, Name = "无封面本地歌曲", CoverUrl = cover },
        _ => Task.FromResult(true));

    private static void WaitFor(Func<bool> predicate, string label)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < 3000)
        {
            Dispatcher.UIThread.RunJobs();
            if (predicate()) return;
            Thread.Sleep(10);
        }
        throw new InvalidOperationException(label);
    }
}
