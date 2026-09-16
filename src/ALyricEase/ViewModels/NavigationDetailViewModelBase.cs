namespace ALyricEase.ViewModels;

/// <summary>详情页共享的轻量滚动状态；不持有 ScrollViewer 或任何视觉元素。</summary>
public abstract class NavigationDetailViewModelBase : ViewModelBase
{
    private double _pageScrollOffset;
    private double _pendingScrollRestoreOffset;
    private int _scrollRestoreVersion;

    internal event EventHandler? ScrollRestoreRequested;

    internal double PageScrollOffset => _pageScrollOffset;

    internal void UpdatePageScrollOffset(double offset)
        => _pageScrollOffset = double.IsFinite(offset) ? Math.Max(0, offset) : 0;

    internal bool TryGetPendingScrollRestore(
        int appliedVersion, out int version, out double offset)
    {
        version = _scrollRestoreVersion;
        offset = _pendingScrollRestoreOffset;
        return version > 0 && version != appliedVersion;
    }

    protected void ResetPageScrollState() => _pageScrollOffset = 0;

    protected void RestorePageScrollState(double offset)
    {
        _pageScrollOffset = Math.Max(0, offset);
        _pendingScrollRestoreOffset = _pageScrollOffset;
        _scrollRestoreVersion++;
        ScrollRestoreRequested?.Invoke(this, EventArgs.Empty);
    }
}
