using System;
using System.Diagnostics;
using System.Threading.Tasks;
using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.ViewModels;
using Avalonia.Threading;

namespace ALyricEase.Headless;

/// <summary>
/// "QQ 个别歌单加载不出来"生产路径探针(--qqopen [tid] [dirId] [名])。
/// 在真实 headless 应用 + 真实 DI + 真实网络下驱动生产的 OpenQqPlaylistCommand
/// (与用户点开歌单完全同一条路径:缓存恢复 → 全量拉取 → ApplyFreshRows),
/// 打印装载耗时 / 行数 / Message / IsBusy 终态。不带参数时默认开"听歌吧"+BGM 对照。
/// </summary>
public static class QqOpenProbe
{
    public static async Task<int> RunAsync(long tid, long dirId, string name)
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Console.WriteLine($"[qqopen] UI 线程未处理异常: {e.Exception}");
            e.Handled = true;
        };
        try
        {
            var vm = ServiceLocator.Get<PlaylistViewModel>();
            var pl = new Playlist { Id = tid, DirId = dirId, Source = MusicSource.QQ, Name = name };
            var sw = Stopwatch.StartNew();
            vm.OpenQqPlaylistCommand.Execute(new PlaylistItemViewModel(pl));

            // 等装载结束:IsBusy 回落(或 60s 超时 = 用户看到的"永远转圈")
            var busySeen = vm.IsBusy;
            while (vm.IsBusy && sw.ElapsedMilliseconds < 60_000)
                await Task.Delay(100).ConfigureAwait(true);
            var timedOut = vm.IsBusy; // 60s 后仍在忙 = 卡死
            sw.Stop();

            await Task.Delay(300).ConfigureAwait(true); // 留一拍让尾部 UI 状态落定
            Console.WriteLine($"[qqopen] [{name}] tid={tid} dirId={dirId}: 耗时={sw.ElapsedMilliseconds}ms " +
                $"行数={vm.RetainedTrackRowCount} Tracks={vm.Tracks.Count} 标题=[{vm.PlaylistTitle}] " +
                $"创建者=[{vm.CreatorName}] IsBusy终态={timedOut}(起初={busySeen})");
            Console.WriteLine($"[qqopen] [{name}] Message=[{vm.Message ?? "(无)"}]");
            return timedOut || (vm.RetainedTrackRowCount == 0 && (vm.Message ?? "").Length == 0) ? 1 : 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[qqopen] 异常: {ex}");
            return 1;
        }
    }
}
