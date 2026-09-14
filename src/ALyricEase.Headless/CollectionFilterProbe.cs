using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ALyricEase.Controls;
using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.ViewModels;
using ALyricEase.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ALyricEase.Headless;

/// <summary>排序/筛选共享状态与原版响应式控件的无网络回归探针。</summary>
public static class CollectionFilterProbe
{
    public static int Run()
    {
        try
        {
            VerifyTrackProjection();
            VerifyAlbumProjection();
            VerifyResponsiveControl();
            VerifyPlaylistComposition();
            Console.WriteLine("[collection-filter] PASS");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[collection-filter] FAIL: {ex.Message}");
            return 1;
        }
    }

    private static void VerifyTrackProjection()
    {
        var rows = new List<SongItemViewModel>
        {
            Row(1, "Zulu", "Beta", "Record C"),
            Row(2, "Alpha", "Gamma", "Record B"),
            Row(3, "Beta", "Alpha", "Record A"),
        };
        var state = CollectionSortAndFilterViewModel.ForTracks("在歌曲中搜索");

        state.SelectedSortIndex = 2;
        AssertSequence(state.ApplyToTracks(rows).Select(row => row.Name), "Beta", "Zulu", "Alpha");

        state.SearchText = "record b";
        AssertSequence(state.ApplyToTracks(rows).Select(row => row.Name), "Alpha");

        state.Reset();
        Assert(!state.IsActive && state.SelectedSortIndex == 0 && state.SearchText.Length == 0,
            "重置没有恢复原始状态");
        AssertSequence(state.ApplyToTracks(rows).Select(row => row.Name), "Zulu", "Alpha", "Beta");
    }

    private static void VerifyAlbumProjection()
    {
        var albums = new[]
        {
            new AlbumCardViewModel(1, "Zulu", ""),
            new AlbumCardViewModel(2, "Alpha", ""),
            new AlbumCardViewModel(3, "Beta Live", ""),
        };
        var state = CollectionSortAndFilterViewModel.ForAlbums("在专辑中搜索");
        state.SelectedSortIndex = 1;
        state.SearchText = "a";
        AssertSequence(state.ApplyToAlbums(albums).Select(album => album.Title), "Alpha", "Beta Live");
    }

    private static void VerifyResponsiveControl()
    {
        var state = CollectionSortAndFilterViewModel.ForTracks("在歌单中搜索");
        var control = new TrackCollectionSortAndFilterControl { DataContext = state };
        var window = new Window { Width = 820, Height = 160, Content = control };
        window.Show();
        Drain();
        Assert(ReferenceEquals(control.DataContext, state), "控件没有接入筛选状态");
        Assert(control.Classes.Contains("wide"), "宽布局没有激活");

        window.Width = 500;
        Drain();
        Assert(!control.Classes.Contains("wide"), "窄布局没有激活");
        window.Close();
    }

    private static void VerifyPlaylistComposition()
    {
        var vm = ServiceLocator.Get<PlaylistViewModel>();
        vm.SelectedPlaylist = new PlaylistItemViewModel(new Playlist
        {
            Name = "筛选布局测试歌单",
            TrackCount = 120,
            Description = "用于确认展开控件保持在头部容器内部。",
        });
        vm.CreatorName = "测试用户";
        vm.Filters.IsExpanded = false;

        var view = new PlaylistView { DataContext = vm };
        var window = new Window { Width = 520, Height = 760, Content = view };
        window.Show();
        Drain();

        var hero = view.FindControl<Border>("HeroCard")
                   ?? throw new InvalidOperationException("找不到歌单头部");
        var filter = hero.GetVisualDescendants()
            .OfType<TrackCollectionSortAndFilterControl>().SingleOrDefault()
            ?? throw new InvalidOperationException("歌单头部缺少排序筛选控件");
        var toggle = hero.GetVisualDescendants().OfType<ToggleButton>()
            .Single(button => button.Classes.Contains("filter-expander") && button.IsEffectivelyVisible);
        var collapsedPosition = toggle.TranslatePoint(new Point(0, 0), window)
                                ?? throw new InvalidOperationException("无法读取收起按钮坐标");
        var collapsedLocalPosition = toggle.TranslatePoint(new Point(0, 0), hero)
                                     ?? throw new InvalidOperationException("无法读取按钮在头部内的坐标");
        var collapsedHeroHeight = hero.Bounds.Height;
        var collapsedBottomGap = collapsedHeroHeight - collapsedLocalPosition.Y - toggle.Bounds.Height;
        Assert(collapsedBottomGap <= 30,
            $"折叠状态仍有多余底部占位：{collapsedBottomGap:F1}px");

        vm.Filters.IsExpanded = true;
        Drain();
        var expandedPosition = toggle.TranslatePoint(new Point(0, 0), window)
                               ?? throw new InvalidOperationException("无法读取展开按钮坐标");
        Assert(Math.Abs(collapsedPosition.X - expandedPosition.X) < 0.5
               && Math.Abs(collapsedPosition.Y - expandedPosition.Y) < 0.5,
            $"展开前后按钮发生位移：{collapsedPosition} -> {expandedPosition}");
        Assert(hero.Bounds.Height > collapsedHeroHeight,
            "展开筛选区后头部没有向下增长");
        Assert(filter.IsVisible && filter.Bounds.Width > 0 && filter.Bounds.Height > 0,
            "歌单排序筛选控件展开后没有参与布局");
        var combo = filter.FindControl<ComboBox>("SortCombo")!;
        var search = filter.FindControl<TextBox>("SearchBox")!;
        Assert(combo.ItemCount == 4 && combo.SelectedIndex == 0,
            $"排序选项绑定异常：ItemCount={combo.ItemCount}, SelectedIndex={combo.SelectedIndex}");
        Assert(search.PlaceholderText == "在歌单中搜索", "搜索占位提示没有绑定到页面状态");
        var filterTop = filter.TranslatePoint(new Point(0, 0), hero)?.Y ?? double.NaN;
        Assert(filterTop >= 0 && filterTop + filter.Bounds.Height <= hero.Bounds.Height + 0.5,
            "歌单排序筛选控件超出了头部容器");

        var screenshot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aly-collection-filter.png");
        using (var bitmap = new RenderTargetBitmap(
                   new PixelSize((int)window.ClientSize.Width, (int)window.ClientSize.Height),
                   new Vector(96, 96)))
        {
            bitmap.Render(window);
            bitmap.Save(screenshot, PngBitmapEncoderOptions.Default);
        }
        Console.WriteLine($"[collection-filter] 截图: {screenshot}");
        window.Close();
        vm.SelectedPlaylist = null;
        vm.Filters.IsExpanded = false;
    }

    private static SongItemViewModel Row(long id, string name, string artist, string album) =>
        new(new Song { Id = id, Name = name, Artist = artist, Album = album },
            _ => Task.FromResult(true), index: (int)id);

    private static void Drain()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
    }

    private static void AssertSequence(IEnumerable<string> actual, params string[] expected)
    {
        var values = actual.ToArray();
        Assert(values.SequenceEqual(expected),
            $"序列不符：实际 [{string.Join(", ", values)}]，期望 [{string.Join(", ", expected)}]");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
