using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ALyricEase.Infrastructure;
using ALyricEase.ViewModels;
using ALyricEase.Views;

namespace ALyricEase.Headless;

/// <summary>歌手页关注按钮 Fluent 2 两态回归。
/// 纯 UI 检查,不发任何网络请求:Accent/Subtle 两套填充+描边、原版字体 Add/Check 字形、
/// 写请求进行中不加进度环(只禁用+降透明度)、各态图标位一致、与更多按钮的间距。
/// 颜色不硬编码,只比较两态是否真的换了一套画刷,避免明暗主题差异造成误判。</summary>
public static class ArtistFollowProbe
{
    private const string AddGlyph = "\uE92B";
    private const string CheckGlyph = "\uE92C";
    private static int _failed;

    public static int Run()
    {
        var vm = ServiceLocator.Get<ArtistViewModel>();
        vm.HasArtist = true;
        vm.IsFollowStateLoaded = true;
        vm.IsFollowBusy = false;
        vm.IsFollowed = false;

        var win = new Window { Width = 1100, Height = 700, Content = new ArtistView { DataContext = vm } };
        win.Show();
        Drain();

        var button = win.GetVisualDescendants().OfType<Button>()
            .First(b => b.Classes.Contains("follow-button"));
        var more = win.GetVisualDescendants().OfType<Button>()
            .First(b => b.Classes.Contains("more-button"));
        var glyphs = button.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.Classes.Contains("follow-glyph")).ToList();
        var iconGrid = glyphs[0].GetVisualParent() as Grid;
        var label = button.GetVisualDescendants().OfType<TextBlock>()
            .First(t => t.Classes.Contains("follow-label"));
        var iconTextGap = iconGrid is null ? double.NaN : label.Bounds.Left - iconGrid.Bounds.Right;
        // 关键回归:按钮里不允许再出现任何进度环 / 轨道环。
        var rings = button.GetVisualDescendants().OfType<Ellipse>()
            .Where(e => e.Classes.Contains("spinner") || e.Classes.Contains("follow-spinner-track"))
            .ToList();

        var restBg = Brush(button.Background);
        var restFg = Brush(button.Foreground);
        var restBorder = Brush(button.BorderBrush);
        var restGlyphFg = Brush(glyphs[0].Foreground);
        var spacing = more.Bounds.Left - (button.Bounds.Left + button.Bounds.Width);
        var restIconLeft = iconGrid is null ? double.NaN : IconLeft(iconGrid, button);
        var restRightInset = RightInset(label, button);
        var restWidth = button.Bounds.Width;

        Console.WriteLine($"[artist-follow] 未关注: bg={restBg} fg={restFg} border={restBorder} " +
            $"图标色={restGlyphFg} 文案={vm.FollowLabel} 尺寸={button.Bounds.Width:F0}x{button.Bounds.Height:F0} " +
            $"与更多按钮间距={spacing:F0} 图标位X={restIconLeft:F1} 图文盒间距={iconTextGap:F1} " +
            $"左留白={restIconLeft:F1} 右留白={restRightInset:F1} " +
            $"加号={glyphs[0].IsEffectivelyVisible} 勾={glyphs[1].IsEffectivelyVisible} " +
            $"进度环数={rings.Count} 可点={button.IsEnabled}");
        Check("字体图标有两个(加号/勾)", glyphs.Count == 2);
        Check("加号=原版 Add U+E92B", glyphs[0].Text == AddGlyph);
        Check("勾=原版 Check U+E92C", glyphs[1].Text == CheckGlyph);
        Check("图标绑到 FluentUISystemIcons 且字号 16",
            (glyphs[0].FontFamily?.ToString() ?? "").Contains("FluentUISystemIcons")
            && Math.Abs(glyphs[0].FontSize - 16) < 0.1);
        Check("图标位 16x16",
            iconGrid is not null
            && Math.Abs(iconGrid.Bounds.Width - 16) < 0.6
            && Math.Abs(iconGrid.Bounds.Height - 16) < 0.6);
        Check("按钮高度 36", Math.Abs(button.Bounds.Height - 36) < 0.6);
        Check("与更多按钮间距 10", Math.Abs(spacing - 10) < 0.6);
        Check("图标与文案盒间距 6", Math.Abs(iconTextGap - 6) < 0.6);
        // 核心回归:按钮左右留白必须对称(= 12 内边距 + 1 描边),不允许右侧多出一截空白。
        Check($"左右留白对称(左={restIconLeft:F1} 右={restRightInset:F1})",
            Math.Abs(restIconLeft - restRightInset) < 0.6);
        Check($"右侧留白就是 12 内边距+1 描边(实测 {restRightInset:F1})", Math.Abs(restRightInset - 13) < 0.8);
        Check("未关注显示加号", glyphs[0].IsEffectivelyVisible && !glyphs[1].IsEffectivelyVisible);
        Check("关注按钮内没有进度环", rings.Count == 0);
        Check("文案=关注", vm.FollowLabel == "关注");
        SaveShot(win, "artist-follow-rest.png");

        vm.IsFollowed = true;
        Drain();
        var followedBg = Brush(button.Background);
        var followedFg = Brush(button.Foreground);
        var followedBorder = Brush(button.BorderBrush);
        var followedGlyphFg = Brush(glyphs[0].Foreground);
        var followedIconLeft = iconGrid is null ? double.NaN : IconLeft(iconGrid, button);
        var followedRightInset = RightInset(label, button);
        Console.WriteLine($"[artist-follow] 已关注: bg={followedBg} fg={followedFg} border={followedBorder} " +
            $"图标色={followedGlyphFg} 文案={vm.FollowLabel} 尺寸={button.Bounds.Width:F0}x{button.Bounds.Height:F0} " +
            $"图标位X={followedIconLeft:F1} 右留白={followedRightInset:F1} " +
            $"加号={glyphs[0].IsEffectivelyVisible} 勾={glyphs[1].IsEffectivelyVisible}");
        Check("两态背景不同", restBg != followedBg);
        Check("两态前景不同", restFg != followedFg);
        Check("两态描边不同", restBorder != followedBorder);
        Check("两态图标颜色不同", restGlyphFg != followedGlyphFg);
        Check("已关注显示勾", !glyphs[0].IsEffectivelyVisible && glyphs[1].IsEffectivelyVisible);
        Check("两态图标位一致", Math.Abs(restIconLeft - followedIconLeft) < 0.6);
        Check($"已关注左右留白同样对称(左={followedIconLeft:F1} 右={followedRightInset:F1})",
            Math.Abs(followedIconLeft - followedRightInset) < 0.6);
        Check("按钮宽度随文案变宽(不是靠 MinWidth 撑死)",
            button.Bounds.Width > restWidth + 8);
        Check("文案=已关注", vm.FollowLabel == "已关注");
        SaveShot(win, "artist-follow-followed.png");

        vm.IsFollowBusy = true;
        Drain();
        var busyIconLeft = iconGrid is null ? double.NaN : IconLeft(iconGrid, button);
        Console.WriteLine($"[artist-follow] 写请求中: 进度环数={rings.Count} 图标位X={busyIconLeft:F1} " +
            $"(未关注={restIconLeft:F1} 已关注={followedIconLeft:F1} 位移={busyIconLeft - restIconLeft:F1}px) " +
            $"加号={glyphs[0].IsEffectivelyVisible} 勾={glyphs[1].IsEffectivelyVisible} " +
            $"透明度={button.Opacity:F2} 可点={button.IsEnabled}");
        Check("写请求中仍显示目标态图标", glyphs[1].IsEffectivelyVisible && !glyphs[0].IsEffectivelyVisible);
        Check("写请求中没有进度环", rings.Count == 0);
        Check("写请求中禁用按钮", !button.IsEnabled);
        Check("写请求中降透明度反馈", Math.Abs(button.Opacity - 0.6) < 0.05);
        Check("写请求中图标位不变", Math.Abs(restIconLeft - busyIconLeft) < 0.6);
        SaveShot(win, "artist-follow-busy.png");

        // 明暗双主题:语义资源必须随 RequestedThemeVariant 真正切换,而不是只有 Light 好看
        vm.IsFollowBusy = false;
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = ThemeVariant.Dark;
            Drain();
            var darkBg = Brush(button.Background);
            var darkFg = Brush(button.Foreground);
            var darkGlyphFg = Brush(glyphs[0].Foreground);
            Console.WriteLine($"[artist-follow] 暗色已关注: bg={darkBg} fg={darkFg} " +
                $"border={Brush(button.BorderBrush)} 图标色={darkGlyphFg}");
            Check("暗色填充与亮色不同", darkBg != followedBg);
            Check("暗色前景与亮色不同", darkFg != followedFg);
            Check("暗色图标色与亮色不同", darkGlyphFg != followedGlyphFg);
            SaveShot(win, "artist-follow-dark.png");

            vm.IsFollowBusy = true;
            Drain();
            SaveShot(win, "artist-follow-busy-dark.png");
            vm.IsFollowBusy = false;
        }

        win.Close();
        Console.WriteLine(_failed == 0 ? "[artist-follow] 全部通过" : $"[artist-follow] 失败 {_failed} 项");
        return _failed == 0 ? 0 : 1;
    }

    private static string Brush(IBrush? brush)
        => brush is SolidColorBrush solid ? solid.Color.ToString() : brush?.ToString() ?? "null";

    /// <summary>图标容器左上角在按钮坐标系里的 X,用来比各态是否真的占同一个位置。</summary>
    private static double IconLeft(Visual icon, Visual button)
        => icon.TranslatePoint(new Point(0, 0), button)?.X ?? double.NaN;

    /// <summary>内容右边缘到按钮右边缘的距离(含 1px 描边),必须等于左留白才算左右对称。</summary>
    private static double RightInset(Visual content, Visual button)
    {
        var right = content.TranslatePoint(new Point(content.Bounds.Width, 0), button)?.X ?? double.NaN;
        return button.Bounds.Width - right;
    }

    private static void Check(string label, bool ok)
    {
        if (!ok) { _failed++; Console.Error.WriteLine($"  ! {label}"); }
    }

    private static void SaveShot(Window win, string name)
    {
        try
        {
            var px = new PixelSize((int)win.ClientSize.Width, (int)win.ClientSize.Height);
            using var rtb = new RenderTargetBitmap(px, new Vector(96, 96));
            rtb.Render(win);
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), name);
            rtb.Save(path, PngBitmapEncoderOptions.Default);
            Console.WriteLine($"  截图: {path}");
        }
        catch (Exception ex) { Console.WriteLine($"  截图跳过({ex.GetType().Name})"); }
    }

    private static void Drain()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
    }
}
