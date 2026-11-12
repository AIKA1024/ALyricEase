using System.Reflection;
using System.Text;
using System.Text.Json;
using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.Services.QQMusic;
using ALyricEase.ViewModels;
using ALyricEase.Views;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ALyricEase.Headless;

/// <summary>侧栏"本地音乐歌单"右键菜单回归(--local-pl-menu):
/// ① 本地歌单能拿到菜单(此前被 OnNavListContextRequested 的 else 分支吞掉,右键毫无反应),
///    普通导航项仍为空、聚合分支不变;
/// ② 菜单项确实接到命令(用 RaiseEvent 触发 Click,不是绕过菜单直接调 VM);
/// ③ 重命名换实例后:资料库/state.json/聚合成员兜底名/已打开详情页的引用全部跟上;
/// ④ 删除后:实体与封面移除、聚合成员引用摘除、零成员聚合一并移除、详情页复位;
/// ⑤ 创建弹窗从平台模式切到本地模式后不得留"设为隐私歌单"(单例 VM 计算属性漏通知),
///    但切到 QQ 音乐必须**保留**(创建时设隐私走 AddPlaylist.dirShow=2);
/// ⑥ 离线验 QQ 创建歌单的 param:隐私带 dirShow=2 + 官网其余字段,公开只带 dirName;
/// ⑦ 验自己创建的私密歌单不会被"他人隐私歌单"的一律抛错判据误伤。
/// ⚠ 重命名/删除都会 AppState.Save(),先把 DI 单例的落盘路径改到临时文件,不碰用户 state.json。</summary>
internal static class LocalPlaylistMenuProbe
{
    public static async Task<int> RunAsync()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"aly-local-pl-{Guid.NewGuid():N}.json");
        try
        {
            var vm = ServiceLocator.Get<MainViewModel>();
            var state = vm.AppState;
            typeof(AppStateStore).GetField("_path", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(state, temp);
            state.LocalPlaylists.Clear();
            state.AggregatePlaylists.Clear();

            var local = new LocalPlaylist
            {
                Id = "probe-lp",
                Name = "本地测试歌单",
                Tracks = [@"C://music//a.mp3"],
            };
            state.LocalPlaylists.Add(local);
            var mixed = new AggregatePlaylist
            {
                Id = "agg-mixed",
                Name = "混合聚合",
                Members =
                [
                    new AggregatePlaylistMember { Source = MusicSource.NetEase, PlaylistId = 7, PlaylistName = "云" },
                    new AggregatePlaylistMember { Source = MusicSource.Local, LocalPlaylistId = local.Id, PlaylistName = local.Name },
                ],
            };
            var onlyLocal = new AggregatePlaylist
            {
                Id = "agg-only",
                Name = "仅本地聚合",
                Members =
                [
                    new AggregatePlaylistMember { Source = MusicSource.Local, LocalPlaylistId = local.Id, PlaylistName = local.Name },
                ],
            };
            state.AggregatePlaylists.Add(mixed);
            state.AggregatePlaylists.Add(onlyLocal);

            // ① 菜单分支
            var nav = new NavItemViewModel($"LocalPlaylist:{local.Id}", local.Name, "", localPlaylist: local);
            var menu = NavigationPaneView.BuildContextMenu(nav, vm);
            var headers = menu?.Items.Cast<object>().OfType<MenuItem>()
                .Select(item => item.Header as string).ToArray() ?? [];
            var localMenuOk = headers.SequenceEqual(["重命名歌单", "歌单设置", "删除歌单"]);
            var plainHasNoMenu = NavigationPaneView.BuildContextMenu(
                new NavItemViewModel("Settings", "设置"), vm) is null;
            var aggregateMenu = NavigationPaneView.BuildContextMenu(
                new NavItemViewModel("Aggregate:mixed", mixed.Name, aggregate: mixed), vm);
            var aggregateUnchanged = aggregateMenu?.Items.Cast<object>().OfType<MenuItem>()
                .Any(item => item.Header as string == "选择成员歌单") == true;

            // ② "歌单设置"接线(复用详情页齿轮的封面弹窗)
            Click(menu!, "歌单设置");
            var settingsOk = vm.IsLocalPlaylistSettingsDialogOpen
                && vm.LocalPlaylistSettingsDialog.LocalPlaylistName == local.Name;
            vm.CloseLocalPlaylistSettingsDialogCommand.Execute(null);

            // 打开详情页,后面验重命名/删除时它是否同步换引用或复位
            await vm.Playlist.OpenLocalPlaylistCommand.ExecuteAsync(local);
            var detailOpened = ReferenceEquals(vm.Playlist.CurrentLocalPlaylist, local);

            // ③ 重命名
            Click(menu!, "重命名歌单");
            var renameOpen = vm.IsRenamePlaylistDialogOpen
                && vm.RenamePlaylistDialog.SourceLabel == "本地音乐"
                && vm.RenamePlaylistDialog.Name == local.Name;
            vm.RenamePlaylistDialog.Name = "改名后";
            await vm.RenamePlaylistDialog.ConfirmCommand.ExecuteAsync(null);
            var fresh = state.LocalPlaylists.Single();
            var renamed = fresh.Name == "改名后" && fresh.Id == local.Id && fresh.Tracks.Count == 1
                && !ReferenceEquals(fresh, local)
                && mixed.Members[1].PlaylistName == "改名后"
                && onlyLocal.Members[0].PlaylistName == "改名后"
                && !vm.IsRenamePlaylistDialogOpen;
            var detailFollows = detailOpened
                && ReferenceEquals(vm.Playlist.CurrentLocalPlaylist, fresh)
                && vm.Playlist.PlaylistTitle == "改名后";
            var reloaded = new AppStateStore(temp);
            var persisted = reloaded.LocalPlaylists.Count == 1
                && reloaded.LocalPlaylists[0].Name == "改名后"
                && reloaded.LocalPlaylists[0].Tracks.Count == 1;

            // ③b 菜单"歌单设置"保存封面必须写回 state.json(此前回调漏了 AppState.Save)。
            fresh.CustomCover = "local-probe.img";
            vm.OpenLocalPlaylistSettingsCommand.Execute(fresh);
            vm.LocalPlaylistSettingsDialog.SaveCommand.Execute(null);
            var settingsPersisted = new AppStateStore(temp).LocalPlaylists
                .Single().CustomCover == "local-probe.img";

            // ④ 删除(用换新实例后的 nav,模拟侧栏重建后的行)
            var freshNav = new NavItemViewModel($"LocalPlaylist:{fresh.Id}", fresh.Name, "", localPlaylist: fresh);
            var deleteMenu = NavigationPaneView.BuildContextMenu(freshNav, vm)!;
            Click(deleteMenu, "删除歌单");
            var deleteOpen = vm.IsDeletePlaylistDialogOpen
                && vm.DeletePlaylistDialog.SourceLabel == "本地音乐"
                && vm.DeletePlaylistDialog.ConfirmText.Contains("音频文件不会被删除");
            await vm.DeletePlaylistDialog.ConfirmCommand.ExecuteAsync(null);
            var deleted = state.LocalPlaylists.Count == 0
                && mixed.Members.Count == 1
                && mixed.Members.All(m => m.LocalPlaylistId != local.Id)
                && state.AggregatePlaylists.Count == 1
                && !state.AggregatePlaylists.Contains(onlyLocal)
                && vm.Playlist.CurrentLocalPlaylist is null
                && !vm.IsDeletePlaylistDialogOpen;
            var reloadedAfterDelete = new AppStateStore(temp);
            var persistedDelete = reloadedAfterDelete.LocalPlaylists.Count == 0
                && reloadedAfterDelete.AggregatePlaylists.Count == 1;

            // ⑤ 创建弹窗是单例 VM:先开过网易云(勾选框可见)再切本地,必须真的隐藏且发通知。
            // 只验 ShowIsPrivate 的 getter 不够 —— 绑定靠 PropertyChanged 才会重算。
            var create = vm.CreatePlaylistDialog;
            var changed = new List<string>();
            System.ComponentModel.PropertyChangedEventHandler onChanged = (_, e) =>
            {
                if (e.PropertyName is { } p) changed.Add(p);
            };
            create.PropertyChanged += onChanged;
            create.Refresh(MusicSource.NetEase);
            var neShowsPrivate = create.ShowIsPrivate && create.SourceLabel == "网易云";
            changed.Clear();
            create.RefreshLocal();
            var localNoPrivate = !create.ShowIsPrivate && create.SourceLabel == "本地音乐";
            var localNotified = changed.Contains(nameof(CreatePlaylistDialogViewModel.ShowIsPrivate))
                && changed.Contains(nameof(CreatePlaylistDialogViewModel.SourceLabel));
            create.Refresh(MusicSource.QQ);
            // QQ 音乐也支持创建时设隐私(dirShow=2),早期误判为"接口无此参数"而把勾选框藏了
            var qqShowsPrivate = create.ShowIsPrivate && create.SourceLabel == "QQ音乐";
            create.PropertyChanged -= onChanged;
            var createDialogOk = neShowsPrivate && localNoPrivate && localNotified && qqShowsPrivate;

            // ⑥ QQ 创建歌单的写入参数:离线验 dirShow=2,不联网、不碰真实账号
            static string WriteParam(string name, bool isPrivate)
            {
                using var stream = new MemoryStream();
                using (var writer = new Utf8JsonWriter(stream))
                {
                    writer.WriteStartObject();
                    QQMusicApiClient.WriteCreatePlaylistParam(writer, name, isPrivate);
                    writer.WriteEndObject();
                }
                return Encoding.UTF8.GetString(stream.ToArray());
            }
            var privateParam = WriteParam("私密测试", true);
            var plainParam = WriteParam("公开测试", false);
            // ⚠ Utf8JsonWriter 默认把中文转义成 \uXXXX,别用字符串 Contains 比中文值
            using var privateDoc = JsonDocument.Parse(privateParam);
            var root = privateDoc.RootElement;
            var qqParamOk = root.TryGetProperty("dirShow", out var dirShow) && dirShow.GetInt32() == 2
                && root.TryGetProperty("dirName", out var dirName) && dirName.GetString() == "私密测试"
                && root.TryGetProperty("dirDesc", out var dirDesc) && dirDesc.GetString() == ""
                && root.TryGetProperty("dirPicUrl", out var dirPicUrl) && dirPicUrl.GetString() == ""
                && root.TryGetProperty("taglist", out var taglist) && taglist.GetString() == ""
                && !plainParam.Contains("dirShow")
                && !plainParam.Contains("dirDesc");

            // ⑦ 自己建的私密歌单不能被"他人隐私"判据误伤(见 user-page.md §5.1)
            var privacyPageOk =
                QQMusicApiClient.ResolveEmptyPageError(2, 0, 204, 0) == "该歌单被主人设为隐私，仅本人可见"
                && QQMusicApiClient.ResolveEmptyPageError(2, 3, 3, 0) is null   // 本人私密有歌
                && QQMusicApiClient.ResolveEmptyPageError(2, 0, 0, 0) is null   // 刚建的空私密歌单
                && QQMusicApiClient.ResolveEmptyPageError(1, 0, 10, 0) is not null // 全下架兜底
                && QQMusicApiClient.ResolveEmptyPageError(2, 0, 204, 300) is null; // 非首页不判

            var ok = localMenuOk && plainHasNoMenu && aggregateUnchanged && settingsOk
                && renameOpen && renamed && detailFollows && persisted && settingsPersisted
                && deleteOpen && deleted && persistedDelete && createDialogOk && qqParamOk
                && privacyPageOk;
            Console.WriteLine($"[local-pl-menu] menu={localMenuOk} plainNoMenu={plainHasNoMenu} "
                + $"aggregate={aggregateUnchanged} settings={settingsOk} renameOpen={renameOpen} "
                + $"renamed={renamed} detailFollows={detailFollows} persisted={persisted} "
                + $"settingsPersisted={settingsPersisted} deleteOpen={deleteOpen} "
                + $"deleted={deleted} persistedDelete={persistedDelete} "
                + $"createDialog={createDialogOk} qqParam={qqParamOk} privacyPage={privacyPageOk} "
                + $"=> {(ok ? "PASS" : "FAIL")}");
            return ok ? 0 : 1;
        }
        finally
        {
            try { File.Delete(temp); } catch { /* 临时状态删不掉无碍 */ }
        }
    }

    /// <summary>按标题找菜单项并触发 Click,验证"菜单项 → 命令"确实接上了。</summary>
    private static void Click(MenuFlyout menu, string header)
    {
        var item = menu.Items.Cast<object>().OfType<MenuItem>()
            .First(m => m.Header as string == header);
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    }
}
