using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ALyricEase.Controls;
using ALyricEase.Models;
using ALyricEase.Views;

namespace ALyricEase.Headless;

/// <summary>逐字歌词渲染回归(--karaoke):detail 模板里当前行是 <see cref="KaraokeText"/>,
/// 用像素统计验证"亮区随 PositionMs 前进、非当前行保持单色"。
///
/// <para>判据:位置 0 时亮像素≈0;到第 3 个字中间时亮像素显著增多;整行唱完后最多。
/// 同时检查 `IsActive` 绑定(`$parent[ListBoxItem].IsSelected`)是否真的生效 ——
/// 这条绑定失败时控件会静默退回单色,只有像素数能抓到。</para>
/// </summary>
internal static class KaraokeProbe
{
    public static int Run()
    {
        var words = new List<LyricWord>();
        const string text = "深海中那点光";
        long t = 0;
        foreach (var ch in text)
        {
            words.Add(new LyricWord(ch.ToString(), t, t + 200));
            t += 200;
        }

        var first = new LyricLine(TimeSpan.Zero, text) { Words = words };
        var second = new LyricLine(TimeSpan.FromSeconds(5), "下一句");
        var view = new LyricView { FontScale = 1 };
        view.Classes.Add("detail");

        var list = view.FindControl<ListBox>("LyricList")!;
        list.ItemsSource = new[] { first, second };

        var window = new Window
        {
            Width = 600,
            Height = 300,
            Background = Brushes.Black,
            Content = view,
        };

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            list.SelectedIndex = 0;
            Dispatcher.UIThread.RunJobs();
            view.UpdateLayout();

            if (list.ContainerFromIndex(0) is not ListBoxItem container)
                throw new InvalidOperationException("第 0 行容器未物化。");

            var karaoke = container.GetVisualDescendants().OfType<KaraokeText>().FirstOrDefault()
                          ?? throw new InvalidOperationException("detail 模板里没有 KaraokeText。");
            if (!karaoke.IsActive)
                throw new InvalidOperationException("IsActive 绑定失败:当前行没有进入逐字模式。");
            CheckSelectionIndicator(list);

            Console.WriteLine($"[karaoke] container.Opacity={container.Opacity} classes=[{string.Join(",", container.Classes)}] " +
                              $"active={karaoke.IsActive} accent={karaoke.AccentBrush} dim={karaoke.DimBrush} " +
                              $"font={karaoke.FontSize} text=“{karaoke.Text}”");
            // 选中行的 Opacity 有 0.5s 过渡(0.4→1),同步探针等不到它结束;
            // 这里直接给局部值 1,只为让像素统计落在"真实稳态"上。
            container.Transitions = null;   // 断开 0.5s 的 Opacity 过渡,同步探针才能拿到稳态
            container.Opacity = 1;
            Dispatcher.UIThread.RunJobs();
            Console.WriteLine($"[karaoke] 稳态 opacity={container.Opacity}");
            karaoke.IsPlaying = false; // 不让它外推,位置完全由探针给定

            var atStart = Capture(window, "0ms", karaoke, 0);
            var atMid = Capture(window, "500ms", karaoke, 500);
            var atEnd = Capture(window, "1200ms", karaoke, 1200);

            Console.WriteLine($"[karaoke] 亮像素: 0ms={atStart.Lit} 500ms={atMid.Lit} 1200ms={atEnd.Lit}");

            if (atStart.Lit != 0)
                throw new InvalidOperationException($"位置 0 不应有已唱像素,实际 {atStart.Lit}。");
            if (atMid.Lit <= atStart.Lit)
                throw new InvalidOperationException("推进到 500ms 后亮区没有变大。");
            if (atEnd.Lit <= atMid.Lit)
                throw new InvalidOperationException("整行唱完后亮区没有继续变大。");

            // 切到第二行:第一行应退回单色(lit 变成整行,不再有半亮)
            list.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            view.UpdateLayout();
            if (karaoke.IsActive)
                throw new InvalidOperationException("切行后旧当前行仍是 IsActive。");
            CheckSelectionIndicator(list);

            Console.WriteLine("[karaoke] PASS: 亮区随位置前进,切行后退出逐字模式");
            CheckWrapped();
            CheckProgressUpdates(words);
            Task.Run(QqLyricProbe.RunAsync).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[karaoke] FAIL: {ex.Message}");
            return 1;
        }
    }

    private static void CheckSelectionIndicator(ListBox list)
    {
        if (list.ContainerFromIndex(list.SelectedIndex) is not ListBoxItem { IsSelected: true } row)
            throw new InvalidOperationException("当前歌词行未选中。");
        if (row.GetVisualDescendants().OfType<Control>().Any(c => c.Name == "SelectionIndicator" && c.IsEffectivelyVisible))
            throw new InvalidOperationException("当前歌词行左侧出现了列表选中标记。");
    }

    private static void CheckProgressUpdates(IReadOnlyList<LyricWord> words)
    {
        var row = new KaraokeText { Text = "深海中那点光", IsActive = true };
        var window = new Window { Width = 400, Height = 100, Content = row };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
            Dispatcher.UIThread.RunJobs();

            if (row.IsFramePendingForProbe)
                throw new InvalidOperationException("选中的普通歌词排入了动画帧。");

            // 读取 Avalonia 自带的测试钩子，直接检查控件是否进入重绘队列。
            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var renderer = typeof(TopLevel).GetProperty("Renderer", flags)!.GetValue(window)!;
            var pending = renderer.GetType().GetMethod("HasPendingUpdateForUnitTests", flags)!
                .CreateDelegate<Func<Visual, bool>>(renderer);

            row.PositionMs = 200;
            if (pending(row))
                throw new InvalidOperationException("普通歌词因进度采样重绘。");

            var layoutField = typeof(KaraokeText).GetField("_litLayout", flags)!;
            var layout = layoutField.GetValue(row);
            row.InvalidateMeasure();
            window.UpdateLayout();
            if (layout is null || !ReferenceEquals(layout, layoutField.GetValue(row)))
                throw new InvalidOperationException("不限高、排版参数相同的歌词未复用排版。");

            row.FontFamily = new FontFamily("Consolas");
            window.UpdateLayout();
            if (ReferenceEquals(layout, layoutField.GetValue(row)))
                throw new InvalidOperationException("更换字体后仍在使用旧排版。");

            row.Words = words;
            if (!row.IsFramePendingForProbe)
                throw new InvalidOperationException("当前行收到逐字数据后没有启动动画。");
            row.Words = [];
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
            Dispatcher.UIThread.RunJobs();
            if (row.IsFramePendingForProbe)
                throw new InvalidOperationException("移除逐字数据后动画仍在排帧。");

            row.IsActive = false;
            row.Words = words;
            row.IsPlaying = false;
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
            Dispatcher.UIThread.RunJobs();
            row.PositionMs = 700;
            if (pending(row))
                throw new InvalidOperationException("非当前逐字行因进度采样重绘。");
            row.IsActive = true;
            if (row.RenderedPositionMsForProbe != 700)
                throw new InvalidOperationException("切到逐字行时丢失最近的进度采样。");
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
            Dispatcher.UIThread.RunJobs();
            row.PositionMs = 300;
            if (!pending(row) || row.RenderedPositionMsForProbe != 300 || row.IsFramePendingForProbe)
                throw new InvalidOperationException("暂停拖动后应立即按新位置重绘一次。");

            Console.WriteLine("[karaoke] PASS: 普通歌词停帧、静态行跳过重绘、暂停拖动与排版复用");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>折行回归:窗口窄到一句折两行,验证第二行的裁剪用的是"行首字符索引"
    /// 而不是"行起点 x 偏移"(TextLine.Start 是后者,左对齐单行永远为 0,曾是隐藏 bug)。
    /// 判据:0ms 两行都暗;500ms 只有上半行亮;1200ms 两行都有亮。</summary>
    private static void CheckWrapped()
    {
        const string text = "深海中那点光";
        var words = new List<LyricWord>();
        long t = 0;
        foreach (var ch in text)
        {
            words.Add(new LyricWord(ch.ToString(), t, t + 200));
            t += 200;
        }

        var line = new LyricLine(TimeSpan.Zero, text) { Words = words };
        var view = new LyricView { FontScale = 1 };
        view.Classes.Add("detail");
        var list = view.FindControl<ListBox>("LyricList")!;
        list.ItemsSource = new[] { line };

        var window = new Window
        {
            Width = 150,
            Height = 260,
            Background = Brushes.Black,
            Content = view,
        };

        window.Show();
        Dispatcher.UIThread.RunJobs();
        list.SelectedIndex = 0;
        Dispatcher.UIThread.RunJobs();
        view.UpdateLayout();

        if (list.ContainerFromIndex(0) is not ListBoxItem container)
            throw new InvalidOperationException("折行探针:第 0 行容器未物化。");
        container.Transitions = null;
        container.Opacity = 1;
        Dispatcher.UIThread.RunJobs();

        var karaoke = container.GetVisualDescendants().OfType<KaraokeText>().First();
        karaoke.IsPlaying = false;

        var (t0, b0) = Bands(window, karaoke, 0, "wrap-0");
        var (t1, b1) = Bands(window, karaoke, 500, "wrap-500");
        var (t2, b2) = Bands(window, karaoke, 1200, "wrap-1200");
        Console.WriteLine($"[karaoke:wrap] 0ms={t0}/{b0} 500ms={t1}/{b1} 1200ms={t2}/{b2} (上/下)");

        if (t0 != 0 || b0 != 0)
            throw new InvalidOperationException("折行:0ms 不应有已唱像素。");
        if (t1 <= 0 || b1 != 0)
            throw new InvalidOperationException($"折行:500ms 应只亮上半行,实际 上={t1} 下={b1}。");
        if (t2 <= 0 || b2 <= 0)
            throw new InvalidOperationException($"折行:1200ms 两行都应亮,实际 上={t2} 下={b2}。");

        window.Close();
        Console.WriteLine("[karaoke:wrap] PASS: 第二行按字符索引裁剪");
    }

    /// <summary>按控件矩形的上下两半分别统计亮像素。</summary>
    private static (int Top, int Bottom) Bands(Window window, KaraokeText karaoke, long positionMs, string name)
    {
        karaoke.PositionMs = positionMs;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var size = new PixelSize((int)window.ClientSize.Width, (int)window.ClientSize.Height);
        using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
        bitmap.Render(window);
        bitmap.Save(Path.Combine(Path.GetTempPath(), $"aly-karaoke-{name}.png"),
            PngBitmapEncoderOptions.Default);

        var stride = size.Width * 4;
        var buffer = new byte[stride * size.Height];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(
            buffer, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, size.Width, size.Height),
                handle.AddrOfPinnedObject(), buffer.Length, stride);
        }
        finally
        {
            handle.Free();
        }

        var origin = karaoke.TranslatePoint(default, window) ?? default;
        var x0 = Math.Clamp((int)origin.X, 0, size.Width);
        var x1 = Math.Clamp((int)(origin.X + karaoke.Bounds.Width), 0, size.Width);
        var y0 = Math.Clamp((int)origin.Y, 0, size.Height);
        var y1 = Math.Clamp((int)(origin.Y + karaoke.Bounds.Height), 0, size.Height);
        var mid = (y0 + y1) / 2;

        int top = 0, bottom = 0;
        for (var y = y0; y < y1; y++)
        {
            var rowStart = y * stride;
            for (var x = x0; x < x1; x++)
            {
                var i = rowStart + x * 4;
                if (i + 3 >= buffer.Length) break;
                if ((buffer[i] + buffer[i + 1] + buffer[i + 2]) / 3 <= 130) continue;
                if (y < mid) top++;
                else bottom++;
            }
        }

        return (top, bottom);
    }

    /// <summary>真实数据版(--karaoke-real [songId]):走线上 yrc → LrcParser 合并 → 真实 detail 模板,
    /// 验证"真实歌词也能出双色"。合成数据那条只证明控件会画,这条才证明数据接得上。</summary>
    public static int RunReal(long songId, bool qq = false)
    {
        ALyricEase.Models.LyricResult? lrc;
        try
        {
            if (qq)
            {
                var api = new Services.QQMusic.QQMusicApiClient(
                    new Services.Auth.CookieStore(Path.Combine(Path.GetTempPath(), $"qrc-{Guid.NewGuid():N}.json")));
                var song = api.GetSongDetailAsync(songId).GetAwaiter().GetResult()
                    ?? throw new InvalidOperationException("QQ 音乐没有返回歌曲详情。");
                lrc = api.GetLyricAsync(song).GetAwaiter().GetResult();
            }
            else
            {
                var api = new ALyricEase.Services.NetEase.NetEaseApiClient(
                    new ALyricEase.Services.Crypto.CryptoService(),
                    new ALyricEase.Services.NetEase.CnIpPool(),
                    new ALyricEase.Services.Auth.CookieStore());
                lrc = api.GetLyricAsync(songId).GetAwaiter().GetResult();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[karaoke-real] FAIL: 取歌词异常 {ex.Message}");
            return 1;
        }

        if (lrc is null || (!qq && string.IsNullOrEmpty(lrc.Yrc)))
        {
            Console.Error.WriteLine($"[karaoke-real] FAIL: id={songId} 没有返回逐字歌词。");
            return 1;
        }

        var doc = ALyricEase.Services.Lrc.LrcParser.Parse(lrc.Original, lrc.Translation, lrc.Yrc);
        Console.WriteLine($"[karaoke-real] 总行数={doc.Lines.Count} 逐字行={doc.Lines.Count(l => l.HasWordTiming)} 翻译行={doc.Lines.Count(l => l.HasTranslation)}");
        // 挑"中间"那条带字级时间的行:开头几行常是版权/制作 credits,虽然也有 yrc,
        // 但验证渲染时更想看到歌词正文。
        var timed = new List<int>();
        for (var i = 0; i < doc.Lines.Count; i++)
        {
            if (doc.Lines[i] is { HasWordTiming: true, Words.Count: >= 5 })
                timed.Add(i);
        }

        var index = timed.Count > 0 ? timed[timed.Count / 2] : -1;
        if (index < 0)
        {
            Console.Error.WriteLine("[karaoke-real] FAIL: 合并后没有任何一行带字级时间。");
            return 1;
        }

        var line = doc.Lines[index];
        var words = line.Words!;
        Console.WriteLine($"[karaoke-real] id={songId} 行 index={index} “{line.Original}” 共 {words.Count} 字");

        var view = new LyricView { FontScale = 1 };
        view.Classes.Add("detail");
        var list = view.FindControl<ListBox>("LyricList")!;
        list.ItemsSource = doc.Lines;

        var window = new Window
        {
            Width = 900,
            Height = 400,
            Background = Brushes.Black,
            Content = view,
        };

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            // 列表是虚拟化的:目标行不在首屏时容器不会物化。请 ListBox 自己滚过去;
            // 万一仍拿不到,就沿候选行往下退,直到这一条落到视口里。
            ListBoxItem? container = null;
            var preferred = index;
            foreach (var candidate in new[] { preferred }.Concat(timed.Where(i => i != preferred)))
            {
                list.SelectedIndex = candidate;
                list.ScrollIntoView(candidate);
                Dispatcher.UIThread.RunJobs();
                view.UpdateLayout();
                if (list.ContainerFromIndex(candidate) is ListBoxItem materialized)
                {
                    index = candidate;
                    container = materialized;
                    break;
                }
            }

            if (container is null)
                throw new InvalidOperationException("没有任何带字级时间的行被物化。");

            line = doc.Lines[index];
            words = line.Words!;
            Console.WriteLine($"[karaoke-real] 实际渲染行 index={index} “{line.Original}” 共 {words.Count} 字");

            var karaoke = container.GetVisualDescendants().OfType<KaraokeText>().FirstOrDefault()
                          ?? throw new InvalidOperationException("detail 模板里没有 KaraokeText。");
            if (!karaoke.IsActive)
                throw new InvalidOperationException("IsActive 绑定失败:当前行没有进入逐字模式。");

            container.Transitions = null;
            container.Opacity = 1;
            Dispatcher.UIThread.RunJobs();
            karaoke.IsPlaying = false; // 位置完全由探针给定

            var before = Capture(window, $"real{index}-before", karaoke, words[0].StartMs - 200);
            var mid = Capture(window, $"real{index}-mid", karaoke,
                (words[1].StartMs + words[1].EndMs) / 2);
            var after = Capture(window, $"real{index}-after", karaoke, words[^1].EndMs + 100);

            Console.WriteLine($"[karaoke-real] 亮像素: 开头={before.Lit} 中间={mid.Lit} 唱完={after.Lit}");

            if (before.Lit != 0)
                throw new InvalidOperationException($"行开始前不应有已唱像素,实际 {before.Lit}。");
            if (mid.Lit <= before.Lit)
                throw new InvalidOperationException("推进到第 2 个字后亮区没有变大。");
            if (after.Lit <= mid.Lit)
                throw new InvalidOperationException("整行唱完后亮区没有继续变大。");

            Console.WriteLine("[karaoke-real] PASS: 线上逐字歌词驱动双色高亮");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[karaoke-real] FAIL: {ex.Message}");
            return 1;
        }
    }

    private static (int Lit, int Dim) Capture(Window window, string state, KaraokeText karaoke, long positionMs)
    {
        karaoke.PositionMs = positionMs;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var size = new PixelSize((int)window.ClientSize.Width, (int)window.ClientSize.Height);
        using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
        bitmap.Render(window);

        var path = Path.Combine(Path.GetTempPath(), $"aly-karaoke-{state}.png");
        bitmap.Save(path, PngBitmapEncoderOptions.Default);

        var stride = size.Width * 4;
        var buffer = new byte[stride * size.Height];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(buffer, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, size.Width, size.Height), handle.AddrOfPinnedObject(), buffer.Length, stride);
        }
        finally
        {
            handle.Free();
        }

        // 只统计这一行控件自己的矩形:真实歌词列表里其它行也是白的,
        // 全窗口统计会把"别的行"算成已唱像素。
        var origin = karaoke.TranslatePoint(new Point(0, 0), window) ?? default;
        var x0 = Math.Clamp((int)origin.X, 0, size.Width);
        var y0 = Math.Clamp((int)origin.Y, 0, size.Height);
        var x1 = Math.Clamp((int)(origin.X + karaoke.Bounds.Width), 0, size.Width);
        var y1 = Math.Clamp((int)(origin.Y + karaoke.Bounds.Height), 0, size.Height);

        int lit = 0, dim = 0;
        for (var y = y0; y < y1; y++)
        {
            var row = y * stride;
            for (var x = x0; x < x1; x++)
            {
                var i = row + x * 4;
                if (i + 3 >= buffer.Length) break;
                var lum = (buffer[i] + buffer[i + 1] + buffer[i + 2]) / 3;
                // 选中行的 Opacity 过渡在同步探针里停在 ~0.42,亮层白 255→~108、暗层 102→~43;
                // 用 90 作分界区分"已唱/未唱",不依赖过渡是否走完。
                if (lum > 130) lit++;
                else if (lum > 20) dim++;
            }
        }

        Console.WriteLine($"[karaoke:{state}] rect=({x0},{y0})-({x1},{y1}) lit={lit} dim={dim} {path}");
        return (lit, dim);
    }
}
