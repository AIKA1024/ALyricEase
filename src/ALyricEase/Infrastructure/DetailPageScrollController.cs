using Avalonia.Controls;
using Avalonia.Threading;
using ALyricEase.ViewModels;

namespace ALyricEase.Infrastructure;

/// <summary>把详情页 VM 的轻量滚动快照应用到新建视图；挂树边界负责可靠退订。</summary>
internal sealed class DetailPageScrollController
{
    private readonly UserControl _view;
    private readonly ScrollViewer _scroller;
    private NavigationDetailViewModelBase? _viewModel;
    private int _appliedVersion;
    private int _scheduledVersion;
    private bool _isAttached;

    public DetailPageScrollController(UserControl view, ScrollViewer scroller)
    {
        _view = view;
        _scroller = scroller;
        view.AttachedToVisualTree += OnAttached;
        view.DetachedFromVisualTree += OnDetached;
        view.DataContextChanged += OnDataContextChanged;
        scroller.ScrollChanged += OnScrollChanged;
    }

    private void OnAttached(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        _isAttached = true;
        ObserveViewModel();
        ScheduleRestore();
    }

    private void OnDetached(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        _scheduledVersion = 0;
        StopObservingViewModel();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (!_isAttached) return;
        ObserveViewModel();
        ScheduleRestore();
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
        => (_view.DataContext as NavigationDetailViewModelBase)?
            .UpdatePageScrollOffset(_scroller.Offset.Y);

    private void ObserveViewModel()
    {
        var next = _view.DataContext as NavigationDetailViewModelBase;
        if (ReferenceEquals(next, _viewModel)) return;
        StopObservingViewModel();
        _viewModel = next;
        if (next is not null) next.ScrollRestoreRequested += OnScrollRestoreRequested;
    }

    private void StopObservingViewModel()
    {
        if (_viewModel is not null)
            _viewModel.ScrollRestoreRequested -= OnScrollRestoreRequested;
        _viewModel = null;
    }

    private void OnScrollRestoreRequested(object? sender, EventArgs e) => ScheduleRestore();

    private void ScheduleRestore()
    {
        if (_view.DataContext is not NavigationDetailViewModelBase vm
            || !vm.TryGetPendingScrollRestore(
                _appliedVersion, out var version, out var offset)
            || version == _scheduledVersion)
            return;

        _scheduledVersion = version;
        RestoreAfterLayout(vm, version, offset, attempt: 0);
    }

    private void RestoreAfterLayout(
        NavigationDetailViewModelBase vm, int version, double offset, int attempt)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(_view.DataContext, vm)
                || !_isAttached
                || !vm.TryGetPendingScrollRestore(
                    _appliedVersion, out var latestVersion, out var latestOffset)
                || latestVersion != version)
                return;

            _scroller.UpdateLayout();
            var maximum = Math.Max(0, _scroller.Extent.Height - _scroller.Viewport.Height);
            if (maximum + 1 < latestOffset && attempt < 3)
            {
                RestoreAfterLayout(vm, version, latestOffset, attempt + 1);
                return;
            }

            _scroller.Offset = new(_scroller.Offset.X, Math.Min(latestOffset, maximum));
            _appliedVersion = version;
            _scheduledVersion = 0;
        }, attempt == 0 ? DispatcherPriority.Loaded : DispatcherPriority.Render);
    }
}
