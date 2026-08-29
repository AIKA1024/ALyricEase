using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.ViewModels;
using ALyricEase.Views;

namespace ALyricEase.Headless;

/// <summary>重命名歌单弹窗渲染探针(--renamedlg):预填一个 QQ 测试歌单打开弹窗,
/// 渲染整窗 PNG 供人工核对(标题音源文案/预填名/按钮布局)。不触网、无副作用。</summary>
public static class RenameDialogProbe
{
    public static void Run()
    {
        var main = ServiceLocator.Get<MainViewModel>();
        var item = new PlaylistItemViewModel(new Playlist
        {
            Id = 123456, DirId = 1, Source = MusicSource.QQ, Name = "探针测试歌单", TrackCount = 3,
        });
        main.RenamePlaylistDialog.Refresh(item);
        main.IsRenamePlaylistDialogOpen = true;

        var win = new Window
        {
            Width = 720,
            Height = 520,
            Content = new RenamePlaylistDialogView { DataContext = main },
        };
        win.Show();
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();

        var px = new PixelSize((int)win.ClientSize.Width, (int)win.ClientSize.Height);
        using var rtb = new RenderTargetBitmap(px, new Vector(96, 96));
        rtb.Render(win);
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tmpandroid", "renamedlg.png");
        rtb.Save(Path.GetFullPath(path));
        Console.WriteLine("[renamedlg] 弹窗标题=重命名QQ音乐歌单 预填名=探针测试歌单 渲染完成");
        Console.WriteLine($"[renamedlg] 截图: {Path.GetFullPath(path)}");
    }
}
