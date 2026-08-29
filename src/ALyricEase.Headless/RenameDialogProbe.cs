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

/// <summary>歌单弹窗渲染探针(--renamedlg):预填一个 QQ 测试歌单分别打开重命名弹窗与
/// 删除确认弹窗,渲染整窗 PNG 供人工核对(标题音源文案/预填名/警示文案/按钮布局)。
/// 不触网、无副作用。</summary>
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
        Render(new RenamePlaylistDialogView { DataContext = main }, "renamedlg.png");

        main.DeletePlaylistDialog.Refresh(item);
        Render(new DeletePlaylistDialogView { DataContext = main }, "deldlg.png");

        var aggregate = new AggregatePlaylist { Id = "agg1", Name = "探针聚合歌单" };
        main.RenamePlaylistDialog.Refresh(aggregate);
        Render(new RenamePlaylistDialogView { DataContext = main }, "aggrename.png");

        main.DeletePlaylistDialog.Refresh(aggregate);
        Render(new DeletePlaylistDialogView { DataContext = main }, "aggdel.png");
    }

    private static void Render(Control dialog, string fileName)
    {
        var win = new Window { Width = 720, Height = 520, Content = dialog };
        win.Show();
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();

        var px = new PixelSize((int)win.ClientSize.Width, (int)win.ClientSize.Height);
        using var rtb = new RenderTargetBitmap(px, new Vector(96, 96));
        rtb.Render(win);
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tmpandroid", fileName);
        rtb.Save(Path.GetFullPath(path));
        Console.WriteLine($"[renamedlg] 截图: {Path.GetFullPath(path)}");
    }
}
