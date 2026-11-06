using ALyricEase.Infrastructure;
using ALyricEase.Services;
using ALyricEase.Services.NetEase;
using ALyricEase.Services.QQMusic;
using ALyricEase.ViewModels;
using ALyricEase.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ALyricEase.Headless;

/// <summary>浏览页回归:真实接口拉双源内容 → 渲染三个断点 + 明暗主题截图,
/// 验证(1)两个音源都上屏,(2)平台筛选只换投影不重新请求,(3)无横向溢出,
/// (4)点 QQ 榜单能进歌单页并取到榜单曲目(Playlist.TopId 分支)。</summary>
internal static class BrowseProbe
{
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(60);

    public static async Task<int> RunAsync()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var root = Path.Combine(Path.GetTempPath(), "aly-browse-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var vm = ServiceLocator.Get<BrowseViewModel>();
        _ = vm.EnsureLoadedAsync();
        await WaitUntilAsync(() => !vm.IsLoading, LoadTimeout, "浏览页加载超时").ConfigureAwait(true);

        var ne = vm.ToplistsSection.Items.Count(card => card.Source == MusicSource.NetEase);
        var qq = vm.ToplistsSection.Items.Count(card => card.Source == MusicSource.QQ);
        Console.WriteLine($"[browse] 排行榜 网易云 {ne} 张 / QQ {qq} 张；"
                          + $"推荐歌单 {vm.RecommendedSection.Items.Count}；新歌 {vm.NewSongsSection.Items.Count}；"
                          + $"状态：{vm.StatusMessage ?? "(无)"}");
        Check(ne > 0, "排行榜应含网易云榜单");
        Check(qq > 0, "排行榜应含 QQ 榜单");
        Check(vm.NewSongsSection.Items.Count > 0, "新歌速递应有内容");

        // 同一首歌两源都推时只留网易云那条:两边都是新歌榜,重叠是常态(用户报过"同一首并排两次")。
        // 判据用搜索合并那套归一化:标题 + 歌手归一化后相等才算同一首,不同歌手的同名曲不会误判。
        var newSongs = vm.NewSongsSection.Items.ToList();
        Console.WriteLine($"[browse] 新歌速递 {newSongs.Count} 张:"
                          + string.Join("，", newSongs.Select(card => $"{card.Source}:{card.Title}")));
        var crossDuplicates = newSongs
            .GroupBy(card => SongSearchMerger.NormalizeText(card.Title) + "|"
                             + SongSearchMerger.NormalizeText(card.Subtitle))
            .Where(group => group.Select(card => card.Source).Distinct().Count() > 1)
            .Select(group => string.Join(" / ", group.Select(card => $"{card.Source}:{card.Title}")))
            .ToList();
        Check(crossDuplicates.Count == 0,
            $"新歌速递里两源推了同一首歌（{string.Join("；", crossDuplicates)}）");
        // 推荐歌单两个源都要有:QQ 侧走"你的私荐歌单"(推荐流),不够由歌单广场补位
        var nePlaylists = vm.RecommendedSection.Items.Count(card => card.Source == MusicSource.NetEase);
        var qqPlaylists = vm.RecommendedSection.Items.Count(card => card.Source == MusicSource.QQ);
        Console.WriteLine($"[browse] 推荐歌单 网易云 {nePlaylists} 张 / QQ {qqPlaylists} 张");
        Check(nePlaylists > 0, "推荐歌单应含网易云歌单");
        Check(qqPlaylists > 0, "推荐歌单应含 QQ 私荐歌单");

        // 推荐里不能混进"用户自己的歌单":推荐流里 id=202「最近常听」货架就是本人最近听过的歌单
        // (曾经只按 type=500 抓,结果把自建的"测试""BGM"当推荐推了出来)
        var qqApi = ServiceLocator.Get<QQMusicApiClient>();
        var myPlaylists = new List<ALyricEase.Models.Playlist>();
        try
        {
            myPlaylists = await qqApi.GetUserPlaylistsAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException)
        {
            Console.WriteLine("[browse] 取自己的歌单失败(未登录?),跳过重复检查");
        }
        if (myPlaylists.Count > 0)
        {
            var mine = myPlaylists.Select(playlist => playlist.Name).ToHashSet();
            var leaked = vm.RecommendedSection.Items
                .Where(card => card.Source == MusicSource.QQ && mine.Contains(card.Title)).ToList();
            Console.WriteLine($"[browse] 自己的 QQ 歌单 {myPlaylists.Count} 个；推荐里混入 {leaked.Count} 个");
            Check(leaked.Count == 0,
                $"推荐歌单里不能出现自己的歌单（混入：{string.Join(" / ", leaked.Select(card => card.Title))}）");
        }
        // 交错混排:相邻两张应来自不同平台(第一屏就能同时看到两个源)
        var toplistCards = vm.ToplistsSection.Items.ToList();
        Check(toplistCards.Count < 2 || toplistCards[0].Source != toplistCards[1].Source, "两个源的榜单交错排列");

        // 轮播用过的榜单不再出现在排行榜货架里:同一张封面在一屏内出现两次会被当成重复内容
        var heroKeys = vm.HeroItems.Select(hero => (hero.Source, hero.Title)).ToHashSet();
        var duplicated = toplistCards.Where(card => heroKeys.Contains((card.Source, card.Title))).ToList();
        Console.WriteLine($"[browse] 排行榜 {toplistCards.Count} 张(轮播已占 {vm.HeroItems.Count} 张，重复 {duplicated.Count} 张)");
        Check(duplicated.Count == 0, $"排行榜不能和轮播重复（重复：{string.Join(" / ", duplicated.Select(card => card.Title))}）");
        Check(toplistCards.Count >= 4, "剔除轮播后排行榜仍有一行以上的内容");

        // 顶部海报位:和榜单同源,但只取前几张,两个平台都要有(翻页才有"换平台看"的意义)
        Check(vm.Hero is not null, "海报位有内容");
        Check(vm.HeroItems.Count >= 2, "海报位有多张可翻");
        Check(vm.HeroItems.Select(hero => hero.Source).Distinct().Count() == 2, "海报位候选覆盖两个平台");
        Console.WriteLine($"[browse] 海报位 {vm.HeroItems.Count} 张；首张 {vm.Hero!.Title}（{vm.Hero.Kicker}）"
                          + $" 导语 {vm.Hero.Description.Length} 字 / 元信息「{vm.Hero.MetaLine}」");

        var view = new BrowseView { DataContext = vm };
        var window = new Window { Width = 1120, Height = 900, Content = view };
        window.Bind(Window.BackgroundProperty, new DynamicResourceExtension("AppBackground"));
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        window.Show();
        await SettleAsync(2200).ConfigureAwait(true);

        // 侧栏入口 → 页面:导航键与壳层模板都得接上(缺模板时内容区会渲染成 VM.ToString)
        var main = ServiceLocator.Get<MainViewModel>();
        main.SelectedNav = main.ShellNavItems.First(item => item.Key == "Browse");
        await SettleAsync(80).ConfigureAwait(true);
        Check(main.ActivePage == "Browse", "侧栏「浏览」导航到浏览页");
        Check(ReferenceEquals(main.CurrentContent, vm), "内容区指向浏览页 VM");
        Check(main.CurrentPageTitle == "浏览", "顶部横幅标题为「浏览」");
        var shell = new AppShell { DataContext = main };
        Check(shell.DataTemplates.Any(template => template.Match(vm)), "壳层为浏览页注册了视图模板");

        Capture(window, root, "browse-light");
        CheckNoOverflow(view, "1120px");

        // —— 海报位几何 + 真实 Carousel + 翻页 ——
        var heroBorder = view.GetVisualDescendants().OfType<Border>().First(border => border.Classes.Contains("hero-frame"));
        var carousel = view.GetVisualDescendants().OfType<Carousel>().FirstOrDefault();
        Console.WriteLine($"[browse] 海报位 {heroBorder.Bounds.Width:0}×{heroBorder.Bounds.Height:0}；轮播 {carousel?.ItemCount} 项");
        Check(carousel is not null, "海报位用的是 Carousel 控件");
        Check(carousel!.ItemCount == vm.HeroItems.Count, "轮播项数与海报位候选数一致");
        Check(Math.Abs(heroBorder.Bounds.Height - 340) < 1, "宽屏海报位高 340");
        Check(heroBorder.Bounds.Width > 600, "海报位铺满剩余宽度");
        var heroBody = view.GetVisualDescendants().OfType<Grid>().First(grid => grid.Name == "HeroBody");
        var heroDesc = view.GetVisualDescendants().OfType<TextBlock>().First(tb => tb.Classes.Contains("hero-desc"));
        Console.WriteLine($"[browse] 正文列 {heroBody.Bounds.Width:0}px；导语实际 {heroDesc.Bounds.Width:0}px"
                          + $" / 期望 {heroDesc.DesiredSize.Width:0}px / 限宽 {heroDesc.MaxWidth:0}");
        var dots = view.GetVisualDescendants().OfType<ListBox>().First(list => list.Classes.Contains("hero-dots"));
        Check(dots.ItemCount == vm.HeroItems.Count, "圆点数与海报位候选数一致");
        // 圆点几何:8×8 小点、选中拉成 20×8 胶囊、点间留 6px 缝(WinUI PipsPager 的观感)。
        // ⚠ 缝要靠 ListBoxItem 的 Margin;写成 Padding 会被模板里的 ContentPresenter 一起吃掉。
        await SettleAsync(260).ConfigureAwait(true);
        var dotItems = dots.GetVisualDescendants().OfType<ListBoxItem>().ToList();
        Border Pip(int index) => dotItems[index].GetVisualDescendants().OfType<Border>()
            .First(border => border.Classes.Contains("hero-dot"));
        var pipRest = Pip(1);
        var pipGap = Pip(2).TranslatePoint(default, dots)!.Value.X
                     - (Pip(1).TranslatePoint(default, dots)!.Value.X + Pip(1).Bounds.Width);
        Console.WriteLine($"[browse] 圆点 {pipRest.Bounds.Width:0}×{pipRest.Bounds.Height:0}；"
                          + $"选中 {Pip(0).Bounds.Width:0}×{Pip(0).Bounds.Height:0}；点缝 {pipGap:0}px");
        Check(Math.Abs(pipRest.Bounds.Width - 8) < 0.6 && Math.Abs(pipRest.Bounds.Height - 8) < 0.6, "圆点 8×8");
        Check(Math.Abs(Pip(0).Bounds.Width - 20) < 0.6, "选中圆点拉成 20×8 胶囊");
        Check(pipGap > 3, "圆点之间留得出缝(没有挤成一条线)");
        // 圆点在卡片外面(商店做法):点列表底边必须低于卡片底边
        var dotsY = dots.TranslatePoint(default, view)!.Value.Y;
        var heroBottom = heroBorder.TranslatePoint(default, view)!.Value.Y + heroBorder.Bounds.Height;
        Console.WriteLine($"[browse] 圆点在卡片下方 {dotsY - heroBottom:0}px");
        Check(dotsY >= heroBottom, "圆点落在海报卡片外面");

        // 右侧三宫格:1 通栏 + 2 半格,三张都来自当前筛选的源
        var promoTiles = view.GetVisualDescendants().OfType<Button>()
            .Where(button => button.Classes.Contains("promo") && button.IsEffectivelyVisible).ToArray();
        Console.WriteLine($"[browse] 宣传位 {promoTiles.Length} 张；上 {vm.TopPromo?.Title} / 下 {vm.BottomLeft?.Title} + {vm.BottomRight?.Title}");
        Check(vm.HasPromos, "三张宣传位都齐");
        Check(promoTiles.Length == 3, "宽屏渲染出三张宣传位");
        var side = view.GetVisualDescendants().OfType<Grid>().First(grid => grid.Name == "HeroSideInner");
        Check(side.IsEffectivelyVisible, "宽屏显示右侧三宫格");

        // 宣传位取推荐歌单的尾部:不得和货架最前面那几张撞图(同一摞封面一屏出现两次)
        var promoDup = PromoDuplicatesWithShelfHead(vm);
        Console.WriteLine($"[browse] 宣传位与货架前三张重复 {promoDup.Count} 张"
                          + (promoDup.Count > 0 ? "：" + string.Join(" / ", promoDup) : ""));
        Check(promoDup.Count == 0, "宣传位不重复货架最前面那三张");

        var firstHero = vm.Hero!;
        var nextButton = view.GetVisualDescendants().OfType<Button>()
            .First(button => button.Classes.Contains("hero-nav") && ReferenceEquals(button.Command, vm.NextHeroCommand));
        var prevButton = view.GetVisualDescendants().OfType<Button>()
            .First(button => button.Classes.Contains("hero-nav") && ReferenceEquals(button.Command, vm.PrevHeroCommand));
        Console.WriteLine($"[browse] 翻页钮 左 {prevButton.Bounds.X:0},{prevButton.Bounds.Y:0} {prevButton.Bounds.Width:0}×{prevButton.Bounds.Height:0}"
                          + $" / 右 {nextButton.Bounds.X:0},{nextButton.Bounds.Y:0}；卡片 {heroBorder.Bounds.Width:0}");
        CheckGlyphCentered(prevButton, "海报位左翻页钮");
        CheckGlyphCentered(nextButton, "海报位右翻页钮");
        var nextPoint = nextButton.TranslatePoint(new Point(nextButton.Bounds.Width / 2, nextButton.Bounds.Height / 2), window)!.Value;
        window.MouseDown(nextPoint, MouseButton.Left);
        window.MouseUp(nextPoint, MouseButton.Left);
        // 动画期间取一次合成位移:证明走的是 Carousel.PageTransition(而不是瞬间换内容)
        await Task.Delay(40).ConfigureAwait(true);
        Dispatcher.UIThread.RunJobs();
        var slideX = carousel.GetVisualDescendants().OfType<Visual>()
            .Select(v => ElementComposition.GetElementVisual(v)?.Translation.X ?? 0)
            .Select(x => Math.Abs(x))
            .DefaultIfEmpty(0)
            .Max();
        await SettleAsync(600).ConfigureAwait(true);
        Console.WriteLine($"[browse] 翻页动画位移 {slideX:0}px（合成器）");
        Check(slideX > 1, "翻页由合成器平移动画驱动(PageTransition 生效)");
        Check(!ReferenceEquals(vm.Hero, firstHero), "点右箭头翻到下一张海报");
        Check(vm.HeroIndex == 1 && carousel.SelectedIndex == 1, "轮播与圆点同步翻页");
        Console.WriteLine($"[browse] 翻页后 {vm.Hero!.Title}（{vm.Hero.SourceLabel}）");
        vm.HeroIndex = 0;
        await SettleAsync(120).ConfigureAwait(true);
        Check(ReferenceEquals(vm.Hero, firstHero), "圆点回到第一张");

        // —— 货架左/右箭头:必须真的推动横向 ScrollViewer(点上去没反应等于摆了个假按钮) ——
        var shelfNext = view.GetVisualDescendants().OfType<Button>()
            .First(button => button.Classes.Contains("shelf-nav") && Equals(button.Tag, "next"));
        var shelfPrev = view.GetVisualDescendants().OfType<Button>()
            .First(button => button.Classes.Contains("shelf-nav") && Equals(button.Tag, "prev"));
        CheckGlyphCentered(shelfPrev, "货架左箭头");
        CheckGlyphCentered(shelfNext, "货架右箭头");
        var shelfSection = shelfNext.GetVisualAncestors().OfType<Control>()
            .First(ancestor => ancestor.GetVisualDescendants().OfType<ScrollViewer>().Any());
        var shelfScroller = shelfSection.GetVisualDescendants().OfType<ScrollViewer>().First();
        var beforeOffset = shelfScroller.Offset.X;
        var shelfPoint = shelfNext.TranslatePoint(new Point(shelfNext.Bounds.Width / 2, shelfNext.Bounds.Height / 2), window)!.Value;
        window.MouseDown(shelfPoint, MouseButton.Left);
        window.MouseUp(shelfPoint, MouseButton.Left);
        await SettleAsync(250).ConfigureAwait(true);
        Console.WriteLine($"[browse] 货架箭头 {beforeOffset:0} → {shelfScroller.Offset.X:0}（视口 {shelfScroller.Viewport.Width:0}）");
        Check(shelfScroller.Offset.X > beforeOffset + 50, "点右箭头后货架真的往右滚了");
        shelfScroller.Offset = default;
        await SettleAsync(120).ConfigureAwait(true);

        var cards = VisibleCards(window);
        Check(cards.Length > 0, "宽屏应实化卡片");
        Check(Math.Abs(cards[0].Bounds.Width - 168) < 1, $"宽屏卡片宽度 168（实际 {cards[0].Bounds.Width}）");
        var art = cards[0].GetVisualDescendants().OfType<Border>().First(border => border.Classes.Contains("browse-art"));
        Check(Math.Abs(art.Bounds.Width - 168) < 1 && Math.Abs(art.Bounds.Height - 168) < 1, "封面为 168 正方形");
        Check(cards[0].GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text is "网易云音乐" or "QQ音乐"),
            "封面带平台名角标");

        // —— 平台筛选:只换投影 ——
        var total = vm.ToplistsSection.Items.Count;
        vm.SourceIndex = 1;
        await SettleAsync(120).ConfigureAwait(true);
        Check(vm.ToplistsSection.Items.All(card => card.Source == MusicSource.NetEase), "网易云筛选只剩网易云榜单");
        Check(vm.ToplistsSection.Items.Count == ne, "网易云筛选条目数等于网易云卡片数");
        // 筛选后轮播只剩网易云那几张,货架同样得把它们排掉(规则要跟着筛选走,不能只在"全部"下成立)
        var neHeroTitles = vm.HeroItems.Select(hero => hero.Title).ToHashSet();
        Check(vm.ToplistsSection.Items.All(card => !neHeroTitles.Contains(card.Title)),
            "网易云筛选下排行榜仍不与轮播重复");
        Check(vm.RecommendedSection.Items.All(card => card.Source == MusicSource.NetEase), "网易云筛选只剩网易云推荐歌单");
        Check(vm.RecommendedSection.HasItems, "网易云筛选下推荐歌单仍在");
        Check(vm.Hero?.Source == MusicSource.NetEase, "网易云筛选下海报位也是网易云榜单");
        Check(vm.TopPromo?.Source == MusicSource.NetEase && vm.BottomRight?.Source == MusicSource.NetEase,
            "网易云筛选下三张宣传位都来自网易云");
        Check(PromoDuplicatesWithShelfHead(vm).Count == 0, "网易云筛选下宣传位仍不与货架前三张撞图");
        await SettleAsync(700).ConfigureAwait(true);   // 换源后是新封面,等解码落地再截
        Capture(window, root, "browse-netease");
        vm.SourceIndex = 2;
        await SettleAsync(120).ConfigureAwait(true);
        Check(vm.ToplistsSection.Items.All(card => card.Source == MusicSource.QQ), "QQ 筛选只剩 QQ 榜单");
        Check(vm.RecommendedSection.HasItems
              && vm.RecommendedSection.Items.All(card => card.Source == MusicSource.QQ),
            "QQ 筛选下推荐歌单换成 QQ 私荐歌单");
        Check(vm.Hero?.Source == MusicSource.QQ, "QQ 筛选下海报位换成 QQ 榜单");
        Check(vm.HasPromos && vm.BottomRight?.Source == MusicSource.QQ, "QQ 筛选下宣传位换成 QQ 推荐歌单");
        Check(PromoDuplicatesWithShelfHead(vm).Count == 0, "QQ 筛选下宣传位仍不与货架前三张撞图");
        // 宣传位换源后是几张没解过的封面,等它们落地再截(否则截图里是灰块,看着像坏图)
        await SettleAsync(700).ConfigureAwait(true);
        Capture(window, root, "browse-qq");
        vm.SourceIndex = 0;
        await SettleAsync(120).ConfigureAwait(true);
        Check(vm.ToplistsSection.Items.Count == total, "回到全部恢复完整混排");

        // 往下滚到"推荐歌单":这张图才看得到两个源的歌单真的混排在同一行里
        var pageScroll = view.GetVisualDescendants().OfType<ScrollViewer>()
            .First(scroller => scroller.Classes.Contains("page-scroll"));
        pageScroll.Offset = new Vector(0, 640);
        await SettleAsync(900).ConfigureAwait(true);
        Capture(window, root, "browse-shelves");
        pageScroll.Offset = default;
        await SettleAsync(200).ConfigureAwait(true);

        // —— 暗色 ——
        Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
        await SettleAsync(200).ConfigureAwait(true);
        Capture(window, root, "browse-dark");
        Application.Current.RequestedThemeVariant = ThemeVariant.Light;

        // —— 断点 ——
        foreach (var width in new[] { 760, 620, 480, 360 })
        {
            window.Width = width;
            await SettleAsync(220).ConfigureAwait(true);
            CheckNoOverflow(view, $"{width}px");
            var width0 = VisibleCards(window).FirstOrDefault()?.Bounds.Width ?? 0;
            var hero = view.GetVisualDescendants().OfType<Border>().First(border => border.Classes.Contains("hero-frame"));
            var sideAt = view.GetVisualDescendants().OfType<Grid>().First(grid => grid.Name == "HeroSideInner");
            Console.WriteLine($"[browse] {width}px 卡片宽 {width0:0}；海报位 {hero.Bounds.Width:0}×{hero.Bounds.Height:0}；三宫格 {(sideAt.IsEffectivelyVisible ? "在" : "收起")}");
            Check(hero.Bounds.Width <= width, $"{width}px 海报位没有溢出内容区");
            Check(!sideAt.IsEffectivelyVisible, $"{width}px 三宫格收起、轮播吃满整行");
            Capture(window, root, $"browse-{width}");
            if (width == 760) Check(Math.Abs(hero.Bounds.Height - 300) < 1, "中屏海报位高 300");
            if (width == 360) Check(Math.Abs(hero.Bounds.Height - 230) < 1, "小屏海报位高 230");
        }
        window.Width = 1120;
        await SettleAsync(200).ConfigureAwait(true);

        // —— 点 QQ 榜单 → 歌单页 TopId 分支 ——
        var qqCard = VisibleCards(window).FirstOrDefault(button => button.DataContext is BrowseCardViewModel { Source: MusicSource.QQ });
        if (qqCard is not null)
        {
            var playlist = ServiceLocator.Get<PlaylistViewModel>();
            Console.WriteLine($"[browse] 点击 QQ 榜单：{((BrowseCardViewModel)qqCard.DataContext!).Title}");
            var point = qqCard.TranslatePoint(new Point(qqCard.Bounds.Width / 2, qqCard.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            await WaitUntilAsync(() => playlist.SelectedPlaylist?.Playlist.TopId > 0, TimeSpan.FromSeconds(15),
                "点榜单未进入歌单页").ConfigureAwait(true);
            await WaitUntilAsync(() => !playlist.IsBusy, TimeSpan.FromSeconds(60), "QQ 榜单曲目加载超时").ConfigureAwait(true);
            Check(main.ActivePage == "Favorites", "点榜单进入歌单页");
            Console.WriteLine($"[browse] 榜单曲目 {playlist.Tracks.Count} 首；首曲 {playlist.Tracks.FirstOrDefault()?.Song.Name}");
            Check(playlist.Tracks.Count > 0, "QQ 榜单曲目已取到");
            Check(playlist.Tracks.All(row => row.Song.Source == MusicSource.QQ), "榜单曲目都来自 QQ");
            Check(playlist.Tracks.All(row => row.Song.Mid.Length > 0), "榜单曲目已补 mid（可播放）");
            Check(main.CurrentPageTitle == playlist.SelectedPlaylist!.Name, "横幅标题取榜单名");

            // 榜单页外观:复用歌单页,hero 里应显示榜单名 / QQ音乐 / 曲数
            var page = new PlaylistView { DataContext = playlist };
            var pageWindow = new Window { Width = 1120, Height = 820, Content = page };
            pageWindow.Bind(Window.BackgroundProperty, new DynamicResourceExtension("AppBackground"));
            pageWindow.Show();
            await SettleAsync(1800).ConfigureAwait(true);
            Capture(pageWindow, root, "browse-toplist-page");
            pageWindow.Close();
        }

        // —— 点 QQ 推荐歌单 → 歌单页的普通歌单分支(TopId 留 0,曲目走 uniform_get_Dissinfo) ——
        var qqPlaylistCard = vm.RecommendedSection.Items.FirstOrDefault(card => card.Source == MusicSource.QQ);
        Check(qqPlaylistCard is not null, "推荐歌单里有 QQ 歌单");
        Console.WriteLine($"[browse] 点击 QQ 推荐歌单：{qqPlaylistCard!.Title}");
        await qqPlaylistCard.OpenCommand.ExecuteAsync(null).ConfigureAwait(true);
        var songList = ServiceLocator.Get<PlaylistViewModel>();
        await WaitUntilAsync(
            () => songList.SelectedPlaylist?.Playlist is { Source: MusicSource.QQ } opened
                  && opened.Name == qqPlaylistCard.Title,
            TimeSpan.FromSeconds(15), "点推荐歌单未进入歌单页").ConfigureAwait(true);
        await WaitUntilAsync(() => !songList.IsBusy, TimeSpan.FromSeconds(60), "QQ 歌单曲目加载超时").ConfigureAwait(true);
        Check(songList.SelectedPlaylist!.Playlist.TopId == 0, "推荐歌单走普通歌单分支(TopId 留 0)");
        Check(songList.SelectedPlaylist.Playlist.Source == MusicSource.QQ, "推荐歌单标记为 QQ 源");
        Check(songList.Tracks.Count > 0, "QQ 歌单曲目已取到");
        Check(songList.Tracks.All(row => row.Song.Source == MusicSource.QQ), "歌单曲目都来自 QQ");
        Console.WriteLine($"[browse] QQ 歌单曲目 {songList.Tracks.Count} 首；首曲 {songList.Tracks.FirstOrDefault()?.Song.Name}");

        await CaptureEmptyStateAsync(root).ConfigureAwait(true);
        main.SelectedNav = main.ShellNavItems.First(item => item.Key == "Browse");
        await SettleAsync(120).ConfigureAwait(true);
        await CaptureShellAsync(main, root).ConfigureAwait(true);

        // 轮播开了滑动识别(IsSwipeEnabled):确认它没把手势区里 CTA 的点击吃掉。
        // 放在最后 —— 这一下会切到歌单页,不能影响前面的截图。
        var cta = view.GetVisualDescendants().OfType<Button>().FirstOrDefault(button => button.Classes.Contains("hero-cta"));
        Check(cta is not null, "海报位里有「查看榜单」");
        var pageBefore = main.ActivePage;
        var ctaPoint = cta!.TranslatePoint(new Point(cta.Bounds.Width / 2, cta.Bounds.Height / 2), window)!.Value;
        window.MouseDown(ctaPoint, MouseButton.Left);
        window.MouseUp(ctaPoint, MouseButton.Left);
        await SettleAsync(400).ConfigureAwait(true);
        Console.WriteLine($"[browse] CTA 点击：{pageBefore} → {main.ActivePage}");
        Check(main.ActivePage != pageBefore, "轮播里的「查看榜单」可点(滑动识别没吃掉点击)");

        Console.WriteLine($"[browse] PASS; previews: {root}");
        return 0;
    }

    /// <summary>整壳快照:侧栏 + 内容区 + 播放条一起看(模板/导航/圆角/背景有没有对不上)。</summary>
    private static async Task CaptureShellAsync(MainViewModel main, string root)
    {
        var shell = new AppShell { DataContext = main };
        var window = new Window { Width = 1360, Height = 840, Content = shell };
        window.Bind(Window.BackgroundProperty, new DynamicResourceExtension("AppBackground"));
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        window.Show();
        await SettleAsync(2200).ConfigureAwait(true);
        Capture(window, root, "browse-shell");
        Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
        await SettleAsync(250).ConfigureAwait(true);
        Capture(window, root, "browse-shell-dark");
        Application.Current.RequestedThemeVariant = ThemeVariant.Light;
        window.Close();
    }

    /// <summary>空态/软提示渲染:不发请求的新 VM(首次打开且没网时就是这副样子)。</summary>
    private static async Task CaptureEmptyStateAsync(string root)
    {
        var empty = new BrowseViewModel(
            ServiceLocator.Get<NetEaseApiClient>(),
            ServiceLocator.Get<QQMusicApiClient>(),
            ServiceLocator.Get<DispatcherService>(),
            ServiceLocator.Get<PlayerViewModel>(),
            ServiceLocator.Get<FollowedNewSongService>());
        var view = new BrowseView { DataContext = empty };
        var window = new Window { Width = 900, Height = 620, Content = view };
        window.Bind(Window.BackgroundProperty, new DynamicResourceExtension("AppBackground"));
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        window.Show();
        await SettleAsync(200).ConfigureAwait(true);
        Check(empty.IsEmpty, "未加载时显示空态");
        Capture(window, root, "browse-empty");
        // 回归:折叠的区块曾经靠 ItemsPanel.Spacing 撑出两段 40px,空态卡被顶到页头下方 ~120px 处
        var header = view.GetVisualDescendants().OfType<StackPanel>().First(panel => panel.Name == "PageHeader");
        var emptyCard = view.GetVisualDescendants().OfType<Border>().First(border => border.Classes.Contains("browse-empty"));
        var gap = emptyCard.TranslatePoint(default, view)!.Value.Y - header.TranslatePoint(default, view)!.Value.Y - header.Bounds.Height;
        Console.WriteLine($"[browse] empty: 空态卡与页头间距={gap:0}px");
        Check(gap < 60, "空态不被折叠区块的间距顶下去");
        empty.StatusMessage = "QQ音乐的内容暂时不可用，已显示网易云音乐的内容。";
        await SettleAsync(80).ConfigureAwait(true);
        Capture(window, root, "browse-empty-status");
        window.Close();
    }

    private static Button[] VisibleCards(Window window) => window.GetVisualDescendants().OfType<Button>()
        .Where(button => button.Classes.Contains("browse-card") && button.IsEffectivelyVisible)
        .ToArray();

    private static void CheckNoOverflow(BrowseView view, string label)
    {
        var scroller = view.GetVisualDescendants().OfType<ScrollViewer>().First();
        Check(scroller.Extent.Width <= scroller.Viewport.Width + 1, $"{label} 无横向溢出");
    }

    /// <summary>宣传位里那些同时出现在推荐歌架<b>每源前三张</b>的卡片(返回标题)。
    /// 货架是从头排的,宣传位取头部就会和读者刚看到的封面撞;这里把这条规则钉住。
    /// 卡片实例在池子里只有一份,直接用引用比对。</summary>
    private static List<string> PromoDuplicatesWithShelfHead(BrowseViewModel vm)
    {
        var heads = new HashSet<BrowseCardViewModel>(ReferenceEqualityComparer.Instance);
        foreach (var source in new[] { MusicSource.NetEase, MusicSource.QQ })
            foreach (var card in vm.RecommendedSection.Items.Where(card => card.Source == source).Take(3))
                heads.Add(card);
        return new[] { vm.TopPromo, vm.BottomLeft, vm.BottomRight }
            .Where(card => card is not null && heads.Contains(card))
            .Select(card => card!.Title)
            .ToList();
    }

    /// <summary>按钮里的图标符号必须落在按钮正中。量的是<b>几何</b>的中心而不是 Path 元素的中心:
    /// Shape 拿几何的 Right/Bottom 当期望尺寸,几何不从 (0,0) 起画时元素照样居中、图形却整体偏右下
    /// (货架箭头上一版就偏 3px,元素中心检查抓不到)。</summary>
    private static void CheckGlyphCentered(Button button, string label)
    {
        var path = button.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().First();
        var geometry = path.Data!.Bounds;
        var glyphCenter = new Point(geometry.X + geometry.Width / 2, geometry.Y + geometry.Height / 2);
        var center = path.TranslatePoint(glyphCenter, button)!.Value;
        var dx = center.X - button.Bounds.Width / 2;
        var dy = center.Y - button.Bounds.Height / 2;
        Console.WriteLine($"[browse] {label} 符号偏移 {dx:+0.0;-0.0}×{dy:+0.0;-0.0}px"
                          + $"（按钮 {button.Bounds.Width:0}×{button.Bounds.Height:0}）");
        Check(Math.Abs(dx) < 1 && Math.Abs(dy) < 1, $"{label}的箭头在按钮里居中");
    }

    private static async Task SettleAsync(int milliseconds)
    {
        await Task.Delay(milliseconds);
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string message)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new InvalidOperationException(message);
            await Task.Delay(60);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void Capture(Window window, string root, string name)
    {
        using var image = new RenderTargetBitmap(
            new PixelSize((int)window.ClientSize.Width, (int)window.ClientSize.Height), new Vector(96, 96));
        image.Render(window);
        image.Save(Path.Combine(root, name + ".png"), PngBitmapEncoderOptions.Default);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
