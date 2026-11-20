using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;

namespace ALyricEase.Headless;

/// <summary>核对"文字描边能不能直接用矢量几何做"(--text-geometry)。
///
/// <para>要回答三个问题:
/// 1) <see cref="FormattedText"/> 设了 <c>MaxTextWidth</c> 之后到底会不会折行,
///    <c>BuildGeometry</c> 给的是整段多行轮廓还是单行;
/// 2) <see cref="TextLine"/>/<see cref="DrawableTextRun"/>/<see cref="ShapedTextRun"/>
///    有没有公开的 <c>GlyphRun</c> 可以拿来做逐行轮廓;
/// 3) 几何折行后的行高/居中偏移是否和 <see cref="TextLayout"/> 一致(能不能直接替换现有 8 层副本)。</para>
/// </summary>
internal static class TextGeometryProbe
{
    public static int Run()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        const string text = "谁说对弈平凡的不算英雄，致那黑夜中的呜咽与怒吼";
        var typeface = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
        const double fontSize = 28;
        const double maxWidth = 200;

        // ---- 1) FormattedText + MaxTextWidth ----
        var formatted = new FormattedText(
            text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, fontSize, Brushes.White)
        {
            MaxTextWidth = maxWidth,
            TextAlignment = TextAlignment.Center,
            Trimming = TextTrimming.None,
        };

        var geometry = formatted.BuildGeometry(new Point(0, 0));
        Console.WriteLine(
            $"[text-geo] FormattedText: width={formatted.Width:0.##} height={formatted.Height:0.##} " +
            $"geometry={(geometry is null ? "null" : geometry.GetType().Name)} bounds={(geometry?.Bounds.ToString() ?? "-")}");

        if (geometry is GeometryGroup group)
        {
            Console.WriteLine($"[text-geo] GeometryGroup 子项 {group.Children.Count} 个:");
            foreach (var child in group.Children.Take(6))
                Console.WriteLine($"[text-geo]   child bounds={child.Bounds}");
        }

        // ---- 2) TextLayout 对照 ----
        var layout = new TextLayout(
            text, typeface, fontSize, Brushes.White,
            TextAlignment.Center, TextWrapping.Wrap, TextTrimming.None, null,
            FlowDirection.LeftToRight, maxWidth);
        Console.WriteLine(
            $"[text-geo] TextLayout:    lines={layout.TextLines.Count} width={layout.Width:0.##} height={layout.Height:0.##}");
        foreach (var line in layout.TextLines)
        {
            Console.WriteLine(
                $"[text-geo] tl.line: h={line.Height:0.##} w={line.Width:0.##} start={line.Start:0.##} " +
                $"baseline={line.Baseline:0.##} runs={line.TextRuns.Count}");
        }

        // ---- 2b) TextLayout 的 maxHeight 参数语义 ----
        var longText = string.Concat(Enumerable.Repeat("很长的一句歌词用来验证高度上限。", 6));
        var withHeight = new TextLayout(
            longText, typeface, 48, Brushes.White,
            TextAlignment.Center, TextWrapping.Wrap, TextTrimming.None, null,
            FlowDirection.LeftToRight, 684, 200, double.NaN, 0, 0);
        Console.WriteLine($"[text-geo] maxHeight=200: lines={withHeight.TextLines.Count} height={withHeight.Height:0.##} width={withHeight.Width:0.##}");
        var noHeight = new TextLayout(
            longText, typeface, 48, Brushes.White,
            TextAlignment.Center, TextWrapping.Wrap, TextTrimming.None, null,
            FlowDirection.LeftToRight, 684);
        Console.WriteLine($"[text-geo] 无限高:        lines={noHeight.TextLines.Count} height={noHeight.Height:0.##} width={noHeight.Width:0.##}");

        // ---- 3) 公开成员:能不能拿到 GlyphRun ----
        foreach (var type in new[]
                 {
                     typeof(TextLine), typeof(TextRun), typeof(DrawableTextRun), typeof(ShapedTextRun),
                     typeof(GenericTextRunProperties),
                 })
        {
            var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.Name).ToArray();
            var ctors = type.GetConstructors()
                .Select(c => string.Join(", ", c.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)))
                .ToArray();
            Console.WriteLine($"[text-geo] {type.Name}.props=[{string.Join(",", props)}]");
            Console.WriteLine($"[text-geo] {type.Name}.ctors=[{string.Join(" | ", ctors)}]");
        }

        var firstRun = layout.TextLines[0].TextRuns[0];
        Console.WriteLine($"[text-geo] 首个 run 实际类型 = {firstRun.GetType().FullName}");

        // ---- 3b) 方案二:TextLayout → 每个 run 的 GlyphRun 几何 ----
        // 注意 cast 必须是 ShapedTextRun:DrawableTextRun 上没有 GlyphRun(文档里也没写)。
        var runGeometryGroup = new GeometryGroup { FillRule = FillRule.NonZero };
        var lineY = 0.0;
        foreach (var line in layout.TextLines)
        {
            var runX = line.Start;
            foreach (var run in line.TextRuns)
            {
                if (run is ShapedTextRun shaped)
                {
                    var runGeometry = shaped.GlyphRun.BuildGeometry();
                    if (runGeometry is not null)
                    {
                        runGeometry.Transform = new MatrixTransform(Matrix.CreateTranslation(runX, lineY + line.Baseline));
                        runGeometryGroup.Children.Add(runGeometry);
                    }
                }

                if (run is DrawableTextRun drawable) runX += drawable.Size.Width;
            }

            lineY += line.Height;
        }

        Console.WriteLine(
            $"[text-geo] 方案二 geometry bounds={runGeometryGroup.Bounds} children={runGeometryGroup.Children.Count}");
        if (geometry is not null)
        {
            Console.WriteLine(
                $"[text-geo] 与 FormattedText 几何差: " +
                $"dX={runGeometryGroup.Bounds.X - geometry.Bounds.X:0.###} " +
                $"dY={runGeometryGroup.Bounds.Y - geometry.Bounds.Y:0.###} " +
                $"dW={runGeometryGroup.Bounds.Width - geometry.Bounds.Width:0.###} " +
                $"dH={runGeometryGroup.Bounds.Height - geometry.Bounds.Height:0.###}");
        }

        // ---- 3c) 同一段文字两种画法,导出对比图 ----
        {
            var path = Path.Combine(Path.GetTempPath(), "aly-text-geometry-vs-textlayout.png");
            using var bitmap = new RenderTargetBitmap(new PixelSize(460, 360));
            using (var ctx = bitmap.CreateDrawingContext())
            {
                ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(18, 20, 24)), new Rect(0, 0, 460, 360));
                layout.Draw(ctx, new Point(0, 20));               // 上:TextLayout.Draw(字形栅格化)
                using (ctx.PushTransform(Matrix.CreateTranslation(0, 190)))
                    ctx.DrawGeometry(Brushes.White, null, runGeometryGroup); // 下:几何填充
            }

            bitmap.Save(path, PngBitmapEncoderOptions.Default);
            Console.WriteLine($"[text-geo] 对比图 {path}");
        }

        // ---- 4) 结论式判据 ----
        var single = new TextLayout(
            text, typeface, fontSize, Brushes.White,
            TextAlignment.Left, TextWrapping.NoWrap, TextTrimming.None, null,
            FlowDirection.LeftToRight, double.PositiveInfinity);
        Console.WriteLine($"[text-geo] 单行参照: height={single.Height:0.##} width={single.Width:0.##}");

        var wrapped = geometry is not null && geometry.Bounds.Height > single.Height * 1.5;
        Console.WriteLine(wrapped
            ? "[text-geo] PASS: FormattedText 设 MaxTextWidth 后确实折行, BuildGeometry 给的是多行轮廓"
            : "[text-geo] FAIL: FormattedText 没有折行(几何仍是单行)");

        var hasGlyphRunProp = typeof(DrawableTextRun).GetProperty("GlyphRun") is not null
                              || typeof(ShapedTextRun).GetProperty("GlyphRun") is not null;
        Console.WriteLine(hasGlyphRunProp
            ? "[text-geo] 公开 GlyphRun 属性存在"
            : "[text-geo] 没有公开 GlyphRun 属性: 逐行取轮廓那条路走不通");

        return wrapped ? 0 : 1;
    }
}
