using System.Linq;
using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.ViewModels;
using ALyricEase.Views;
using Avalonia.Controls;

namespace ALyricEase.Headless;

/// <summary>播放条歌曲菜单结构探针(--playerbarmenu)：不触网、不执行命令，验证菜单分组、
/// 多歌手子菜单、来源文本与两平台公开链接，防止动态构造菜单在改样式时悄悄缺项。</summary>
public static class PlayerBarMenuProbe
{
    public static void Run()
    {
        var song = new Song
        {
            Id = 123,
            Name = "空中散步",
            Artist = "CIEL / 测试歌手",
            ArtistIds = new long[] { 10, 11 },
            ArtistNames = new[] { "CIEL", "测试歌手" },
            Album = "空想少女",
            AlbumId = 20,
        };

        var view = new PlayerBarView();
        var menu = view.CreateSongMenu(song, "咸咸的鱼王喜欢的音乐");
        var items = menu.Items.ToList();

        Assert(items.Count == 10, $"一级项数量={items.Count},期望 10");
        Assert(items[0] is MenuItem { Header: "空中散步", IsEnabled: false }, "首项应为禁用歌曲标题");
        Assert(items[1] is MenuItem { Header: "添加到歌单", IsEnabled: true } add && add.Items.Count == 0,
            "添加到歌单应为打开模态窗口的普通菜单项，而不是子菜单");
        Assert(items[2] is MenuItem { Header: "表演者" } artists && artists.Items.Count == 2,
            "多歌手应生成二项子菜单");
        Assert(items[3] is MenuItem { Header: "专辑： 空想少女", IsEnabled: true }, "专辑项不正确");
        Assert(items[4] is Separator && items[8] is Separator, "操作组分隔线不正确");
        Assert(items[5] is MenuItem { Header: "分享", IsEnabled: true }, "分享项不正确");
        Assert(items[6] is MenuItem { Header: "打开浏览器", IsEnabled: true }, "打开浏览器项不正确");
        Assert(items[7] is MenuItem { Header: "复制链接", IsEnabled: true }, "复制链接项不正确");
        Assert(items[9] is MenuItem { Header: "来源： 咸咸的鱼王喜欢的音乐" }, "来源项不正确");
        Assert(menu.FlyoutPresenterClasses.Contains("player-song-menu"), "未应用播放条菜单宽度样式");
        Assert(SongShareLinks.For(song) == "https://music.163.com/song?id=123", "网易云歌曲链接不正确");
        Assert(SongShareLinks.For(new Song { Source = MusicSource.QQ, Mid = "abc" }) ==
               "https://y.qq.com/n/ryqq/songDetail/abc", "QQ歌曲链接不正确");

        var playlists = ServiceLocator.Get<PlaylistViewModel>();
        playlists.Playlists.Add(new PlaylistItemViewModel(new Playlist
        {
            Id = 1, Name = "我的歌单", CanAddTracks = true,
        }));
        playlists.Playlists.Add(new PlaylistItemViewModel(new Playlist
        {
            Id = 2, Name = "收藏歌单", CanAddTracks = false,
        }));
        playlists.QqPlaylists.Add(new PlaylistItemViewModel(new Playlist
        {
            Id = 3, Name = "QQ歌单", Source = MusicSource.QQ, CanAddTracks = true,
        }));
        var dialog = new AddSongToPlaylistDialogViewModel(playlists, () => { });
        dialog.Refresh(song);
        Assert(dialog.Items.Count == 1 && dialog.Items[0].Name == "我的歌单",
            "模态窗口应仅列出歌曲同音源且可写的歌单");
        dialog.SearchText = "不存在";
        Assert(dialog.HasNoItems && dialog.Items.Count == 0, "歌单搜索过滤不正确");

        Console.WriteLine("[playerbarmenu] 菜单结构、模态歌单筛选、歌手子菜单与歌曲链接断言全部通过");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
