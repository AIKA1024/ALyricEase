using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ALyricEase.Infrastructure;
using ALyricEase.Services.QQMusic;
using ALyricEase.ViewModels;
using ALyricEase.Views;

namespace ALyricEase.Headless;

/// <summary>
/// 专辑卡悬停操作探针（--album-card）。回答三件事：
///   ① 歌手页/专辑页共用的 <see cref="Controls.AlbumCard"/> 上确实有 ▶ 与 ••• 两枚操作按钮；
///   ② 卡片按需拉整张曲目能成功（QQ 专辑 mid 走 GetAlbumSongList）；
///   ③ ••• / 右键那份菜单能构造出来（项数与首项文案）。
/// 只读联网。
/// </summary>
internal static class AlbumCardProbe
{
    public static async Task<int> RunAsync()
    {
        var qq = ServiceLocator.Get<QQMusicApiClient>();
        var (_, cookie) = FollowedNewSongProbe.ReadCookies();
        if (cookie.Length > 0) qq.SetCookie(cookie);

        // 鸣潮先约电台的最新一张（order=0 才是时间倒序，见 docs/qq-artist-albums.md）
        var page = await qq.GetArtistAlbumPageAsync(
            "002wbPaC0L9C3A", 0, 1, CancellationToken.None, 0);
        var album = page.FirstOrDefault();
        if (album is null)
        {
            Console.WriteLine("[album-card] FAIL 拿不到专辑");
            return 1;
        }
        Console.WriteLine($"[album-card] 专辑={album.Name} mid={album.Mid} id={album.Id}");

        var card = new AlbumCardViewModel(
            album.Id, album.Name, album.PicUrl, album.Mid, album.PublishTime, album.Size);
        var songs = await card.LoadSongsAsync();
        Console.WriteLine($"[album-card] 曲目={songs.Count} 首 首曲={songs.FirstOrDefault()?.Name ?? "(空)"}");

        var window = new Window
        {
            Width = 260,
            Height = 340,
            Content = new ALyricEase.Controls.AlbumCard { DataContext = card },
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();

        var ops = window.GetVisualDescendants().OfType<Button>()
            .Where(b => b.Classes.Contains("card-op")).ToList();
        var left = ops.FirstOrDefault(b => b.HorizontalAlignment == HorizontalAlignment.Left);
        var right = ops.FirstOrDefault(b => b.HorizontalAlignment == HorizontalAlignment.Right);
        Console.WriteLine($"[album-card] 操作按钮={ops.Count} 播放={(left is not null)} 更多={(right is not null)}"
                          + $" 默认透明度={ops.FirstOrDefault()?.Opacity}");

        if (ops.Count != 2 || left is null || right is null)
        {
            Console.WriteLine("[album-card] FAIL 卡片上缺少 ▶ / ••• 按钮");
            return 1;
        }
        if (songs.Count == 0)
        {
            Console.WriteLine("[album-card] FAIL 拉不到专辑曲目（播放/菜单都无从谈起）");
            return 1;
        }

        // 悬停:headless 里注入不了真实指针,改为确认卡片自带样式里有
        // `Button.album-card:pointerover Button.card-op` 这条浮现规则(与浏览页同款写法)
        var cardControl = window.GetVisualDescendants().OfType<ALyricEase.Controls.AlbumCard>()
            .FirstOrDefault();
        var hoverRules = cardControl?.Styles.OfType<Style>()
            .Count(s => s.Selector?.ToString()?.Contains("card-op", StringComparison.Ordinal) == true) ?? 0;
        Console.WriteLine($"[album-card] 卡片自带 card-op 样式={hoverRules} 条(默认透明度 {left.Opacity:F2})");
        if (hoverRules < 3)
        {
            Console.WriteLine("[album-card] FAIL 缺少悬停浮现/按钮外观样式");
            return 1;
        }

        // 菜单:与 ••• / 右键 / 长按同一份构造逻辑(anchor 只用于定位)
        var menu = AlbumCardContextMenu.Create(right, card.Title, songs, card.PlayAsync);
        var headers = menu.Items.OfType<MenuItem>().Select(i => i.Header?.ToString() ?? "").ToList();
        Console.WriteLine($"[album-card] 菜单项={menu.Items.Count}: {string.Join(" | ", headers)}");
        if (!headers.Any(h => h.StartsWith("播放（", StringComparison.Ordinal))
            || !headers.Contains("打开专辑页"))
        {
            Console.WriteLine("[album-card] FAIL 菜单缺少播放或打开专辑页");
            return 1;
        }

        Console.WriteLine("[album-card] PASS 卡片有 ▶/•••、曲目可拉、菜单结构完整");
        return 0;
    }
}
