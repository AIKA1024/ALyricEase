using System.Collections.Specialized;
using System.Diagnostics;
using System.Reflection;
using ALyricEase.Infrastructure;
using ALyricEase.Models.Dtos;
using ALyricEase.ViewModels;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ALyricEase.Headless;

internal static class PersonalHomeRefreshProbe
{
    public static async Task RunAsync(Window window, PersonalHomeViewModel home, PlaylistViewModel library)
    {
        var main = ServiceLocator.Get<MainViewModel>();
        var split = typeof(PlaylistViewModel).GetMethod("SplitNetEasePlaylists", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var items = Enumerable.Range(1, 120).Select(i => new LegacyPlaylistItem
        {
            Id = 1000 + i, Name = $"歌单 {i}", TrackCount = i,
            CoverUrl = CoverImageConverter.PlaceholderUri, Subscribed = i > 60
        }).ToArray();
        void Apply(LegacyPlaylistItem[] data) => split.Invoke(library, [data]);
        async Task Drain() => await Dispatcher.UIThread.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.Background);
        Apply(items);
        await Drain();
        await Task.Delay(300);
        var originalRows = home.CreatedPlaylists.Concat(home.CollectedPlaylists).ToArray();
        var originalImages = window.GetVisualDescendants().OfType<Image>().ToHashSet();
        var resets = 0;
        var changes = 0;
        var navChanges = 0;
        void Changed(object? sender, NotifyCollectionChangedEventArgs e)
        {
            changes++;
            if (e.Action == NotifyCollectionChangedAction.Reset) resets++;
        }
        home.CreatedPlaylists.CollectionChanged += Changed;
        home.CollectedPlaylists.CollectionChanged += Changed;
        main.ShellNavItems.CollectionChanged += (_, _) => navChanges++;
        var timer = Stopwatch.StartNew();
        Apply(items.Select(i => i with { }).ToArray());
        var synchronousMs = timer.Elapsed.TotalMilliseconds;
        await Drain();
        var retainedRows = home.CreatedPlaylists.Concat(home.CollectedPlaylists).Count(originalRows.Contains);
        var retainedImages = window.GetVisualDescendants().OfType<Image>().Count(originalImages.Contains);
        Console.WriteLine($"[home-refresh] same 120 playlists: sync={synchronousMs:F1}ms settled={timer.Elapsed.TotalMilliseconds:F1}ms " +
                          $"homeChanges={changes} resets={resets} navChanges={navChanges} rows={retainedRows}/{originalRows.Length} images={retainedImages}/{originalImages.Count}");
        if (changes != 0 || navChanges != 0 || retainedRows != originalRows.Length || retainedImages != originalImages.Count)
            throw new InvalidOperationException("Unchanged library refresh rebuilt existing page content.");

        var changed = items.Select(i => i with { }).ToArray();
        changed[0] = changed[0] with { Name = "更新后的歌单", TrackCount = 900 };
        Apply(changed);
        await Drain();
        if (home.CreatedPlaylists[0].Name != "更新后的歌单" || home.CreatedPlaylists[0].TrackCount != 900)
            throw new InvalidOperationException("Metadata did not refresh.");
        if (!main.ShellNavItems.Any(n => n.Label == "更新后的歌单"))
            throw new InvalidOperationException("Navigation label did not refresh.");
        if (!ReferenceEquals(home.CreatedPlaylists[0], originalRows[0]))
            throw new InvalidOperationException("Metadata refresh replaced the existing card.");
        Apply(changed.Skip(1).Reverse().Append(new LegacyPlaylistItem { Id = 2000, Name = "新增歌单" }).ToArray());
        await Drain();
        if (home.CreatedPlaylists.Any(p => p.Id == changed[0].Id) || !home.CreatedPlaylists.Any(p => p.Id == 2000) || resets != 0)
            throw new InvalidOperationException("Incremental insert/remove/reorder failed.");

        var applyQq = typeof(PlaylistViewModel).GetMethod("ApplyQqPlaylists", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var qqRows = library.QqPlaylists.ToArray();
        var qqData = qqRows.Select(p => new ALyricEase.Models.Playlist
        {
            Source = p.Playlist.Source, Id = p.Id, DirId = p.Playlist.DirId,
            Name = p.Name, CoverUrl = p.CoverUrl, TrackCount = p.TrackCount
        }).ToArray();
        changes = navChanges = 0;
        applyQq.Invoke(library, [qqData]);
        await Drain();
        if (!library.QqPlaylists.SequenceEqual(qqRows) || changes != 0 || navChanges != 0)
            throw new InvalidOperationException("QQ refresh rebuilt unchanged items.");
        var selected = main.SelectedNav;
        library.UserName += "更新";
        library.QqUserName += "更新";
        await Drain();
        if (changes != 0 || navChanges != 0 || !ReferenceEquals(selected, main.SelectedNav))
            throw new InvalidOperationException("Profile update rebuilt playlists or changed navigation selection.");
        Console.WriteLine("[home-refresh] PASS: stable controls and incremental metadata/insert/remove/reorder");
    }
}
