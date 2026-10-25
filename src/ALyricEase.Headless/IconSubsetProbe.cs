using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace ALyricEase.Headless;

/// <summary>字体子集自查(--iconsubset):用 App 真实资源里的三个图标字体家族各画一行,
/// 确认子集化之后 avares 路径/#家族名仍能解析、字形没缺(缺了会显示成空白或方块)。</summary>
internal static class IconSubsetProbe
{
    public static void Run(string outputPath)
    {
        var panel = new StackPanel { Spacing = 10, Margin = new Thickness(16), Background = Brushes.White };
        var rows = new (string FamilyKey, string[] Glyphs)[]
        {
            ("LyricEaseIcons", ["\uF610", "\uEB4C", "\uF47E", "\uF151", "\uF6EC"]),
            ("LyricEaseIconsOutline", ["\uF151", "\uF36B", "\uF47A", "\uF6EC"]),
            ("LyricEaseIconsOriginal", ["\uE912", "\uE918", "\uE922", "\uE92B", "\uE94F"]),
        };

        foreach (var (familyKey, glyphs) in rows)
        {
            var label = new TextBlock { Text = familyKey, FontSize = 12, Foreground = Brushes.Gray };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18 };
            if (Application.Current?.FindResource(familyKey) is FontFamily family)
            {
                foreach (var glyph in glyphs)
                    row.Children.Add(new TextBlock
                    {
                        Text = glyph,
                        FontFamily = family,
                        FontSize = 32,
                        Foreground = Brushes.Black,
                    });
            }
            else
            {
                row.Children.Add(new TextBlock { Text = "资源缺失", Foreground = Brushes.Red });
            }
            panel.Children.Add(label);
            panel.Children.Add(row);
        }

        var window = new Window { Width = 420, Height = 260, Content = panel };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();

        using var bitmap = new RenderTargetBitmap(new PixelSize(420, 260), new Vector(96, 96));
        bitmap.Render(window);
        bitmap.Save(outputPath);
        Console.WriteLine($"[iconsubset] {outputPath}");
    }
}
