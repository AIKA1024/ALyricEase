using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using ALyricEase.ViewModels;

namespace ALyricEase.Infrastructure.Behaviors;

/// <summary>
/// 分组开合动画(对齐 WinUI 3 Gallery 的 NavigationView):
/// - 歌单子项本身瞬时出现/消失(布局经 .plchild/.collapsed 类切换 Min/MaxHeight,不带过渡);
/// - 被"挤下去/收回来"的是它们下方的行:布局翻转后,下方行先被反向平移回旧位置,
///   再用 RenderTransform 过渡回 0,观感即从旧位置平滑滑动到新位置。
///
/// 为什么不用合成层的 Offset 动画:ElementComposition 的元素视觉 Offset 由框架在每次
/// dirty 同步时按布局 Bounds 覆写(Visual.SynchronizeCompositionProperties 里的
/// <c>comp.Offset = new(Bounds.Left, Bounds.Top, 0)</c>)。开合必然改变下方行的 Bounds,
/// 于是下一帧 Offset 就被写回新值,动画被整个盖掉——这正是之前"看不见位移"的原因。
/// RenderTransform 反过来是由框架每次同步时从 Visual 读取的(comp.TransformMatrix),
/// 过渡改的是 Transform 自身属性,不会被布局覆写。
/// </summary>
internal static class NavGroupExpandAnimator
{
    /// <summary>基准时长;位移越大给得越长(大分组一次几百像素,太快会糊成一片)。</summary>
    private static readonly TimeSpan MinDuration = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan MaxDuration = TimeSpan.FromMilliseconds(320);
    private const double DurationPerPixel = 0.16; // ms/px
    private const double RowSlotFallback = 44.0;  // 行高 40 + 行距 4(布局没跟上时的兜底值)

    private static readonly Easing EaseOut = new SplineEasing(0.33, 1.0, 0.68, 1.0);

    /// <summary>带动画地执行开合;applyToggle 内部翻转 ShowAsChild/IsExpanded(布局瞬时生效)。</summary>
    public static void Run(ListBox list, NavItemViewModel header, Action applyToggle)
    {
        var rows = OrderedRows(list);
        var headerIndex = rows.FindIndex(x => ReferenceEquals(x.Item, header));
        if (headerIndex < 0)
        {
            applyToggle();
            return;
        }

        // 紧随分组头的连续歌单子项(决定下方行的位移量)
        var childCount = 0;
        for (var i = headerIndex + 1; i < rows.Count; i++)
        {
            if (rows[i].Item.OwnerKey != header.Key || !rows[i].Item.IsPlaylistChild) break;
            childCount++;
        }

        // 需要"被挤/被收回"的行 = 子项之后的所有行;先记下翻转前的行顶位置
        list.UpdateLayout(); // 把挂起的布局先走掉,确保记到的是屏幕上看到的那一帧
        // 记的是"看到的"位置(布局位置 + 当前动画位移),连点时上一次动画被打断也能接得上
        var below = new List<(ListBoxItem Row, double OldY)>();
        for (var i = headerIndex + 1 + childCount; i < rows.Count; i++)
            below.Add((rows[i].Row, VisualY(rows[i].Row)));

        if (childCount == 0 || below.Count == 0)
        {
            applyToggle(); // 空分组 / 下方没有行:没有可动的位移,直接开合
            return;
        }

        // 兜底位移(布局没跟上时用):展开 → 下方行下移,起手为负;收起 → 反之为正
        var fallbackShift = childCount * RowSlotFallback * (header.IsExpanded ? 1.0 : -1.0);

        applyToggle();
        list.UpdateLayout(); // 立刻走一次布局,拿到翻转后的真实位置

        // 起手平移量 = 旧 Y - 新 Y(逐行按真实位置算,不依赖行高常量;
        // 布局没跟上时退回到按行数推算的兜底值)
        var shifts = new List<(ListBoxItem Row, double Shift)>(below.Count);
        var maxShift = 0.0;
        foreach (var (row, oldY) in below)
        {
            var shift = oldY - row.Bounds.Y;
            if (Math.Abs(shift) < 0.5) shift = fallbackShift;
            if (Math.Abs(shift) < 0.5) continue; // 位移为零,没有动画可做
            shifts.Add((row, shift));
            maxShift = Math.Max(maxShift, Math.Abs(shift));
        }
        if (shifts.Count == 0) return;

        var duration = ClampDuration(TimeSpan.FromMilliseconds(140 + maxShift * DurationPerPixel));
        var transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = TranslateTransform.YProperty,
                Duration = duration,
                Easing = EaseOut,
            },
        };

        foreach (var (row, shift) in shifts)
        {
            // 三步:撤过渡 → 瞬移到旧位置 → 挂过渡并回到 0(即新布局位置)。
            // 不撤过渡的话,给 Y 赋起手值本身就会变成一次动画。
            var transform = row.RenderTransform as TranslateTransform ?? new TranslateTransform();
            transform.Transitions = null;
            transform.Y = shift;
            row.RenderTransform = transform;
            transform.Transitions = transitions;
            transform.Y = 0;
        }
    }

    /// <summary>行的实际显示位置(布局位置叠加当前动画位移)。</summary>
    private static double VisualY(ListBoxItem row)
    {
        var animatedOffset = (row.RenderTransform as TranslateTransform)?.Y ?? 0;
        return row.Bounds.Y + animatedOffset;
    }

    private static TimeSpan ClampDuration(TimeSpan value) =>
        value < MinDuration ? MinDuration : value > MaxDuration ? MaxDuration : value;

    private static List<(ListBoxItem Row, NavItemViewModel Item)> OrderedRows(ListBox list) =>
        list.GetRealizedContainers().OfType<ListBoxItem>()
            .Select(r => (Row: r, Item: r.DataContext as NavItemViewModel))
            .Where(x => x.Item is not null)
            .OrderBy(x => list.IndexFromContainer(x.Row))
            .ToList()!;
}
