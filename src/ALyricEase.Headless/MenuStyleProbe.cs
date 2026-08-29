using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace ALyricEase.Headless;

/// <summary>菜单默认样式渲染探针(--menustyle):用与侧栏右键菜单相同的裸 MenuItem
/// 装入与 MenuFlyoutPresenter 等价的圆角表面,渲染 PNG 供人工核对全局默认观感
/// ——行高 32/字号 13/圆角 8/左右内边距 12,应与曲目行歌手·专辑菜单一致。
/// 不直接渲染 MenuFlyoutPresenter:无头渲染器对其 ListBox 层偶发漏绘后续项
/// (诊断显示项文本/几何均正常),静态构图目检项观感已足够。</summary>
public static class MenuStyleProbe
{
    public static void Run()
    {
        var surface = new Border
        {
            Width = 200,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(4),
            Background = new SolidColorBrush(Color.Parse("#FFFBFBFB")),
            Child = new StackPanel
            {
                Children =
                {
                    new MenuItem { Header = "重命名歌单" },
                    new MenuItem { Header = "复制链接" },
                },
            },
        };
        var win = new Window { Width = 260, Height = 130, Content = new StackPanel { Margin = new Thickness(20), Children = { surface } } };
        win.Show();
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();

        var px = new PixelSize((int)win.ClientSize.Width, (int)win.ClientSize.Height);
        using var rtb = new RenderTargetBitmap(px, new Vector(96, 96));
        rtb.Render(win);
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tmpandroid", "menustyle.png");
        rtb.Save(Path.GetFullPath(path));
        Console.WriteLine($"[menustyle] 菜单默认观感渲染完成: {Path.GetFullPath(path)}");
    }
}
