using System;
using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace ALyricEase.Infrastructure;

/// <summary>
/// 可复用页视图模板:同一页多次进出时复用同一个视图实例,而不是每返回一次就新建一个页面视图。
///
/// 背景:内容区由 TransitioningContentControl 按 VM 类型选模板(AppShell 的模板表),每次返回首页都会
/// 新建一个 RecommendView,旧树整批变成垃圾,而 .NET 在内存宽裕时不急着回收 —— 于是任务管理器
/// 看到"每次往返涨一点"。
///
/// **实测边界(别高估它)**:复用的是"视图对象",保住的只是页面骨架(AXAML 里那层 ScrollViewer /
/// StackPanel / 区块列表容器)。首页主体不归它管 —— 视图重新挂回视觉树时,区块列表的容器生成器
/// 会被重置,区块容器、SongGridView 与全部歌曲行照样每轮整体重建。
/// 分阶段实测:返回首页一段的分配 复用 59.4MB vs 不复用 60.0MB(中位数),即只省下约 1%。
/// 要让某页返回时不重建,必须让这棵树**别离开视觉树**(宿主保留 Content、改为隐藏),仅持有实例没用。
/// 详见 docs/avalonia-tips.md「复用视图只能省"骨架"」。
///
/// 适用范围:只给"内容不会因为离开页面而失效"的页用(个性推荐)。歌手/专辑等详情页继续按
/// 离页释放 + 一次性快照恢复的机制重建(见 NavigationDetailViewModelBase),不长期占用内存。
///
/// 安全性:只在视图确实已离开视觉树时才复用。"是否在树上"由视图自身的
/// AttachedToVisualTree / DetachedFromVisualTree 事件跟踪 —— 过渡动画(300ms)进行中时,
/// 上一棵视图仍挂在正在退场的 presenter 里,此时直接复用会让同一个控件被两个父容器持有,
/// 所以另建一个实例顶上,并把缓存槽换成刚上屏的这棵(下一轮返回即可复用)。
/// (不要改用 Parent/VisualParent 判断:ContentPresenter 换内容时不保证清理这些引用。)
/// </summary>
public sealed class ReusablePageViewTemplate : IDataTemplate
{
    private Control? _cached;
    private bool _cachedAttached;

    /// <summary>匹配的数据类型(写法同 DataTemplate.DataType,如 vm:RecommendViewModel)。</summary>
    public Type? DataType { get; set; }

    /// <summary>复用的视图类型,需有公共无参构造(写法同 DataType,如 views:RecommendView)。
    /// ⚠ 必须带 <see cref="DynamicallyAccessedMembersAttribute"/>:这里靠 Activator 反射建实例,
    /// 不标注的话 Release 裁剪会判定"没人 new 过这个构造函数"而把它删掉,运行时在
    /// ContentPresenter 建子元素时抛 MissingMethodException(Arg_NoDefCTor)整机启动即崩
    /// —— 真机实测踩过一次,构建期的 IL2072 警告就是它的预告,别当噪音忽略。</summary>
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
    public Type? ViewType { get; set; }

    public bool Match(object? data) =>
        DataType is not null && data is not null && DataType.IsInstanceOfType(data);

    public Control Build(object? param)
    {
        if (ViewType is null || !typeof(Control).IsAssignableFrom(ViewType))
            throw new InvalidOperationException(
                $"{nameof(ReusablePageViewTemplate)} 需要配置有效的 ViewType(DataType={DataType?.FullName})");

        // 没有缓存,或缓存实例还在树上(过渡退场中) → 另建一棵并把它作为缓存。
        if (_cached is null || _cachedAttached)
        {
            TraceState($"Build: 不复用(缓存为空={_cached is null})");
            Cache(Create());
        }
        else
        {
            TraceState("Build: 复用缓存实例");
        }

        return _cached!;
    }

    /// <summary>性能回归探针使用:当前缓存的视图实例(用于判定复用的确实是同一个对象)。</summary>
    internal Control? CachedView => _cached;

    /// <summary>诊断钩子(默认 null,零开销:未开启时连插值字符串都不拼)。</summary>
    internal static Action<string>? Trace;

    internal void TraceState(string stage)
    {
        if (Trace is not { } trace) return;
        trace($"{stage}: 缓存={(_cached is null ? "无" : _cached.GetType().Name)} 事件在树={_cachedAttached}");
    }

    private void Cache(Control view)
    {
        _cached = view;
        _cachedAttached = false;
        view.AttachedToVisualTree += OnCachedAttached;
        view.DetachedFromVisualTree += OnCachedDetached;
        TraceState("新建并缓存");
    }

    private void OnCachedAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (ReferenceEquals(sender, _cached)) _cachedAttached = true;
        TraceState("Attached");
    }

    private void OnCachedDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (ReferenceEquals(sender, _cached)) _cachedAttached = false;
        TraceState($"Detached(是否当前缓存={ReferenceEquals(sender, _cached)})");
    }

    private Control Create() => (Control)Activator.CreateInstance(ViewType!)!;
}
