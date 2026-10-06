using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ALyricEase.Headless;

/// <summary>长标题菜单布局回归(--menuwidth)：真实菜单模板、桌面 Popup 和 Android overlay。
/// 检查完整圆角表面、文本省略、菜单项实际宽度与子菜单锚点，避免仅裁剪像素而布局仍然过宽。</summary>
public static class MenuWidthProbe
{
    public static void Run()
    {
        Check(false, 1000, true);
        Check(true, 360, true);
        Check(true, 320, true);
        Check(false, 1000, false);
        Console.WriteLine("[menuwidth] PASS");
    }

    public static void RunDesktop()
    {
        Check(false, 1000, true, requireNative: true);
        Check(false, 1000, false, requireNative: true);
        Console.WriteLine("[menuwidth] desktop PASS");
    }

    private static void Check(bool overlay, double windowWidth, bool longTitles, bool requireNative = false)
    {
        var longText = string.Concat(Enumerable.Repeat("很长的歌手与专辑名称 Long Album Title / ", 12));
        var album = new MenuItem { Header = "专辑： " + (longTitles ? longText : "空想少女") };
        var artist = new MenuItem { Header = "表演者", Icon = new TextBlock { Text = "♫" } };
        var child = new MenuItem { Header = longTitles ? longText : "CIEL" };
        artist.Items.Add(child);
        var presenter = new MenuFlyoutPresenter();
        presenter.Classes.Add("player-song-menu");
        presenter.Items.Add(album);
        presenter.Items.Add(artist);

        var anchor = new Button { Content = "菜单", HorizontalAlignment = HorizontalAlignment.Left };
        var panel = new StackPanel { Margin = new Thickness(20), Children = { anchor } };
        var window = new Window { Width = windowWidth, Height = 500, Content = panel };
        window.Show();
        Flush();
        var popup = new Popup
        {
            Child = presenter,
            PlacementTarget = anchor,
            Placement = PlacementMode.BottomEdgeAlignedLeft,
            ShouldUseOverlayLayer = overlay,
        };
        panel.Children.Add(popup);
        popup.IsOpen = true;
        Flush();

        var surface = presenter.GetVisualDescendants().OfType<Border>().Single(x => x.Name == "LayoutRoot");
        var scroller = surface.Child as ScrollViewer ?? throw new InvalidOperationException("没有菜单滚动容器");
        Assert(!overlay || popup.IsUsingOverlayLayer, "没有使用 overlay 宿主");
        Assert(!requireNative || !popup.IsUsingOverlayLayer, "没有使用桌面原生弹窗宿主");
        Assert(scroller.Extent.Width <= scroller.Viewport.Width + 1,
            $"一级菜单横向溢出: extent={scroller.Extent.Width}, viewport={scroller.Viewport.Width}");
        var surfaceOrigin = surface.TranslatePoint(default, presenter)!.Value;
        Assert(surfaceOrigin.X >= 0 && surfaceOrigin.X + surface.Bounds.Width <= presenter.Bounds.Width + 1,
            "一级菜单圆角表面超出宿主");
        Assert(album.Bounds.Width <= scroller.Viewport.Width + 1, "长专辑名撑宽一级菜单项");
        CheckHeader(album, longTitles);
        var chevron = artist.GetVisualDescendants().OfType<Control>().Single(x => x.Name == "PART_ChevronPath");
        var chevronOrigin = chevron.TranslatePoint(default, artist)!.Value;
        Assert(chevron.IsVisible && chevronOrigin.X + chevron.Bounds.Width <= artist.Bounds.Width,
            "子菜单箭头超出菜单项");

        var subPopup = artist.GetVisualDescendants().OfType<Popup>().Single();
        subPopup.ShouldUseOverlayLayer = overlay;
        artist.IsSubMenuOpen = true;
        Flush();
        var subSurface = subPopup.Child as Border ?? throw new InvalidOperationException("没有子菜单表面");
        var subScroller = subSurface.Child as ScrollViewer ?? throw new InvalidOperationException("没有子菜单滚动容器");
        Assert(subScroller.Extent.Width <= subScroller.Viewport.Width + 1,
            $"二级菜单横向溢出: extent={subScroller.Extent.Width}, viewport={subScroller.Viewport.Width}");
        Assert(child.Bounds.Width <= subScroller.Viewport.Width + 1, "长歌手名撑宽二级菜单项");
        CheckHeader(child, longTitles);
        Assert(ReferenceEquals(subPopup.PlacementTarget ?? subPopup.TemplatedParent, artist),
            "二级菜单没有按实际菜单项定位");
        // 桌面有足够空间时，直接检查弹窗相邻边缘；窄屏 overlay 允许平台滑移/翻转。
        if (windowWidth >= 1000)
        {
            var parentRight = artist.PointToScreen(new Point(artist.Bounds.Width, 0)).X;
            var subLeft = subSurface.PointToScreen(default).X;
            var subRight = subSurface.PointToScreen(new Point(subSurface.Bounds.Width, 0)).X;
            var parentLeft = artist.PointToScreen(default).X;
            Assert(Math.Min(Math.Abs(subLeft - parentRight), Math.Abs(subRight - parentLeft)) <= 12,
                $"子菜单未贴实际边缘: parent={parentLeft}..{parentRight}, child={subLeft}..{subRight}");
        }

        var path = Path.Combine(Path.GetTempPath(), $"alyricease-menu-{(overlay ? "overlay" : "desktop")}-{windowWidth}-{longTitles}.png");
        // 单独渲染完整表面，避开入场合成动画，核对右侧圆角与省略号。
        using (var bitmap = new RenderTargetBitmap(
                   new PixelSize((int)Math.Ceiling(presenter.Bounds.Width), (int)Math.Ceiling(presenter.Bounds.Height))))
        {
            bitmap.Render(presenter);
            bitmap.Save(path, new PngBitmapEncoderOptions());
        }
        Console.WriteLine($"[menuwidth] overlay={popup.IsUsingOverlayLayer} window={windowWidth} long={longTitles}: " +
                          $"menu={presenter.Bounds.Width:F0} item={album.Bounds.Width:F0} child={child.Bounds.Width:F0}; {path}");
        artist.IsSubMenuOpen = false;
        popup.IsOpen = false;
        window.Close();
        Flush();
    }

    private static void CheckHeader(MenuItem item, bool longTitle)
    {
        var header = item.GetVisualDescendants().OfType<ContentPresenter>().Single(x => x.Name == "PART_HeaderPresenter");
        var text = header.Child as TextBlock ?? throw new InvalidOperationException("没有标题文本");
        Assert(text.TextTrimming == TextTrimming.CharacterEllipsis, "标题没有使用省略号");
        Assert(text.TextLayout.TextLines.Any(x => x.HasCollapsed) == longTitle,
            $"标题省略结果不正确: long={longTitle}");
    }

    private static void Flush()
    {
        for (var i = 0; i < 4; i++) Dispatcher.UIThread.RunJobs();
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("[menuwidth] " + message);
    }
}
