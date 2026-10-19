using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Models.Dtos;
using ALyricEase.Services;
using ALyricEase.Services.NetEase;
using ALyricEase.ViewModels;
using ALyricEase.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ALyricEase.Headless;

/// <summary>离线验证结果页继续搜索、草稿隔离、音源/分类切换和响应式布局。</summary>
internal static class SearchContinuationProbe
{
    public static int Run()
    {
        try
        {
            foreach (var width in new[] { 360, 700, 1200 }) Verify(width);
            Console.WriteLine("[search-continuation] PASS");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[search-continuation] FAIL {ex}");
            return 1;
        }
    }

    private static void Verify(int width)
    {
        var netEase = new SearchApi(MusicSource.NetEase);
        var qq = new SearchApi(MusicSource.QQ);
        var statePath = Path.Combine(Path.GetTempPath(), $"aly-search-continuation-{Guid.NewGuid():N}.json");
        var vm = new SearchViewModel(new MusicApiProvider([netEase, qq]),
            ServiceLocator.Get<PlayerViewModel>(), new AppStateStore(statePath));
        var view = new SearchView { DataContext = vm };
        var window = new Window
        {
            Width = width, Height = 700, Content = view,
            RequestedThemeVariant = width == 1200 ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light,
        };
        try
        {
            window.Show();
            vm.SelectNetEaseSourceCommand.Execute(null);
            vm.Keyword = "周杰伦";
            vm.SearchCommand.Execute(null);
            Drain();
            var box = view.FindControl<TextBox>("ResultsSearchBox")!;
            Assert(box.IsEffectivelyVisible && box.IsFocused, "首次搜索后结果搜索框可见并获得焦点");
            Assert(netEase.Requests.Last() == ("周杰伦", SearchKind.All), "首次搜索默认全部分类");

            box.Text = "陈奕迅";
            Drain();
            Assert(vm.Keyword == "陈奕迅" && vm.ResultsTitle.Contains("周杰伦"), "编辑草稿不改变当前结果标题");
            vm.SelectedTab = SearchKind.Track;
            Drain();
            Assert(netEase.Requests.Last() == ("周杰伦", SearchKind.Track), "切分类使用已提交关键词");
            box.Focus();
            Enter(window);
            Assert(vm.HasSearched && vm.IsNetEaseSource && vm.SelectedTab == SearchKind.Track &&
                   netEase.Requests.Last() == ("陈奕迅", SearchKind.Track), "Enter 原地搜索并保留分类/音源");
            Assert(vm.SearchHistory.First() == "陈奕迅", "继续搜索更新历史");

            var submit = box.GetVisualDescendants().OfType<Button>()
                .Single(button => ReferenceEquals(button.Command, vm.SearchCommand));
            box.Text = "林俊杰";
            Drain();
            Click(window, submit);
            Assert(netEase.Requests.Last() == ("林俊杰", SearchKind.Track), "鼠标提交新搜索");

            var clear = box.GetVisualDescendants().OfType<Button>()
                .Single(button => ReferenceEquals(button.Command, vm.ClearKeywordCommand));
            Click(window, clear);
            Assert(vm.Keyword == "" && !submit.IsEffectivelyEnabled && vm.HasSearched && vm.SubmittedKeyword == "林俊杰",
                "清空草稿保留结果并禁用空查询提交");
            box.Text = "尚未提交";
            Drain();
            var sourcePicker = view.GetVisualDescendants().OfType<ComboBox>()
                .Single(combo => combo.IsEffectivelyVisible && combo.Classes.Contains("source-selector"));
            sourcePicker.Focus();
            window.KeyPress(Key.Down, RawInputModifiers.Alt, PhysicalKey.ArrowDown, null);
            window.KeyRelease(Key.Down, RawInputModifiers.Alt, PhysicalKey.ArrowDown, null);
            Drain();
            Assert(sourcePicker.IsDropDownOpen, "音源下拉框可以用键盘展开");
            window.KeyPress(Key.End, RawInputModifiers.None, PhysicalKey.End, null);
            window.KeyRelease(Key.End, RawInputModifiers.None, PhysicalKey.End, null);
            Enter(window);
            Assert(vm.IsQQSource && vm.SelectedTab == SearchKind.Track &&
                   qq.Requests.Last() == ("林俊杰", SearchKind.Track), "下拉框选择音源，不提交草稿");
            vm.SelectNetEaseSourceCommand.Execute(null);
            vm.SelectedTab = SearchKind.Album;
            vm.SelectQQSourceCommand.Execute(null);
            Assert(vm.SelectedTab == SearchKind.All && qq.Requests.Last() == ("林俊杰", SearchKind.All),
                "目标音源不支持原分类时回默认分类");
            vm.SelectCombinedSourceCommand.Execute(null);
            Assert(vm.IsCombinedSource && vm.SelectedTab == SearchKind.Track && !vm.ShowTabBar,
                "综合音源使用歌曲分类");

            vm.SelectNetEaseSourceCommand.Execute(null);
            box.Text = "空结果";
            Click(window, submit);
            Assert(vm.ShowEmpty && box.IsEffectivelyVisible, "无结果时仍可继续搜索");
            box.Text = "失败";
            Click(window, submit);
            Assert(vm.Message?.StartsWith("搜索失败") == true && submit.IsEffectivelyEnabled, "失败后可重试");
            box.Text = "很长的搜索关键词也可以在结果页面直接编辑和再次搜索";
            Click(window, submit);
            Assert(vm.Message is null && vm.HasSongs, "错误后新搜索恢复结果");
            for (var index = 2; index <= 30; index++)
                vm.Songs.Add(new SongItemViewModel(
                    new Song { Id = index, Name = $"结果 {index}", Artist = "测试歌手", DurationMs = 180000 },
                    static _ => Task.FromResult(true), index));
            Drain();

            var scroller = view.FindControl<ScrollViewer>("ResultsScroll")!;
            var before = box.TranslatePoint(default, window)!.Value;
            scroller.Offset = new Vector(0, 300);
            Drain();
            Assert(scroller.Offset.Y > 0 && box.TranslatePoint(default, window) == before, "结果滚动不带走搜索入口");
            Assert(scroller.Bounds.Height > 200 && box.Bounds.Width > 220, "窄屏保留搜索与结果空间");
            foreach (var control in view.GetVisualDescendants().OfType<Control>()
                         .Where(control => control.IsEffectivelyVisible &&
                             (control == box || control == sourcePicker)))
            {
                var origin = control.TranslatePoint(default, window)!.Value;
                Assert(origin.X >= 0 && origin.X + control.Bounds.Width <= window.ClientSize.Width + 1,
                    "搜索框与音源下拉框不超出窗口");
            }
            Assert(view.FindControl<Grid>("TabBarHost")!.TranslatePoint(default, window)!.Value.Y < 90,
                "分类和音源集中在两行工具栏内");
            scroller.Offset = default;
            Drain();
            using (var bitmap = new RenderTargetBitmap(new PixelSize(width, 700), new Vector(96, 96)))
            {
                bitmap.Render(window);
                bitmap.Save(Path.Combine(Path.GetTempPath(), $"aly-search-continue-{width}.png"), PngBitmapEncoderOptions.Default);
            }
            scroller.Offset = new Vector(0, 300);
            Drain();
            box.Focus();
            Enter(window);
            Assert(scroller.Offset.Y == 0, "继续搜索后结果滚回顶部");

            // 旧请求返回不能提前结束新请求的加载态，也不能覆盖新结果。
            var older = new TaskCompletionSource<SearchAllResult?>();
            var newer = new TaskCompletionSource<SearchAllResult?>();
            netEase.Pending.Enqueue(older.Task);
            netEase.Pending.Enqueue(newer.Task);
            vm.SelectedTab = SearchKind.Album;
            vm.SelectedTab = SearchKind.Track;
            older.SetResult(new SearchAllResult());
            Drain();
            Assert(vm.IsSearching, "旧请求完成不结束新请求加载态");
            newer.SetResult(SearchApi.Result("最新结果", MusicSource.NetEase));
            Drain();
            Assert(!vm.IsSearching && vm.Songs.Single().Song.Name == "最新结果", "仅接收最新请求结果");
            var abandoned = new TaskCompletionSource<SearchAllResult?>();
            netEase.Pending.Enqueue(abandoned.Task);
            vm.SelectedTab = SearchKind.Album;
            vm.ResetToLanding();
            abandoned.SetResult(SearchApi.Result("过期结果", MusicSource.NetEase));
            Drain();
            Assert(vm.ShowLanding && !vm.IsSearching && vm.Songs.Count == 0, "返回后丢弃在途结果");
            Console.WriteLine($"[search-continuation:{width}] PASS keyboard, pointer, sources, drafts, errors, layout, stale requests");
        }
        finally
        {
            window.Close();
            if (File.Exists(statePath)) File.Delete(statePath);
        }
    }

    private static void Enter(Window window)
    {
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Drain();
    }

    private static void Click(Window window, Control control)
    {
        Drain();
        var center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        Drain();
    }

    private static void Drain()
    {
        for (var i = 0; i < 4; i++) Dispatcher.UIThread.RunJobs();
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class SearchApi(MusicSource source) : IMusicApi
    {
        public MusicSource Source => source;
        public string DisplayName => source.ToString();
        public bool IsLoggedIn => false;
        public bool IsVip => false;
        public bool IsVipLoaded => true;
        public List<(string Keyword, SearchKind Kind)> Requests { get; } = [];
        public Queue<Task<SearchAllResult?>> Pending { get; } = [];
        public Task EnsureVipStatusAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<PlayUrlItem?> GetPlayUrlAsync(Song song, string level = "higher", CancellationToken ct = default) => Task.FromResult<PlayUrlItem?>(null);
        public Task<LyricResult?> GetLyricAsync(Song song, CancellationToken ct = default) => Task.FromResult<LyricResult?>(null);
        public Task<Song?> GetSongDetailAsync(long id, CancellationToken ct = default) => Task.FromResult<Song?>(null);
        public Task<List<Song>> SearchAsync(string keyword, int limit = 30, int offset = 0, CancellationToken ct = default)
        {
            Requests.Add((keyword, SearchKind.Track));
            return Task.FromResult(Result(keyword, source).Songs);
        }

        public Task<SearchAllResult?> SearchAllAsync(string keyword, SearchKind kind, int limit, CancellationToken ct = default)
        {
            Requests.Add((keyword, kind));
            if (Pending.TryDequeue(out var pending)) return pending;
            if (keyword == "失败") throw new ApiException("测试错误");
            return Task.FromResult<SearchAllResult?>(keyword == "空结果" ? new SearchAllResult() : Result(keyword, source));
        }

        public static SearchAllResult Result(string keyword, MusicSource source)
        {
            var result = new SearchAllResult();
            result.Songs.Add(new Song { Id = 1, Name = keyword, Artist = "测试歌手", Source = source, DurationMs = 180000 });
            return result;
        }
    }
}
