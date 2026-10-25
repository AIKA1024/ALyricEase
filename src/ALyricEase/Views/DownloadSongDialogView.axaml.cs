using ALyricEase.ViewModels;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ALyricEase.Views;

public partial class DownloadSongDialogView : UserControl
{
    private TopLevel? _topLevel;
    private InputElement? _previousFocus;
    private Size? _morphFrom;
    private IDisposable? _primedOpacity;
    private CancellationTokenSource? _morphCancellation;
    private CancellationTokenSource? _openingCancellation;
    private IDisposable? _loadingVisibility;
    private IDisposable? _downloadVisibility;
    private bool _openingCommitted;
    private static readonly TimeSpan QueryPresentationDuration = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan MorphDuration = TimeSpan.FromMilliseconds(300);
    private static readonly SplineEasing MorphEasing = new(0.215, 0.61, 0.355, 1);
    internal bool IsMorphing => _morphFrom is not null || _morphCancellation is not null;

    public DownloadSongDialogView()
    {
        InitializeComponent();
        DownloadPanel.PropertyChanged += (_, e) =>
        {
            if (e.Property != IsVisibleProperty) return;
            ResetMorph();
            if (!DownloadPanel.IsVisible || !IsEffectivelyVisible || !IsEffectivelyEnabled
                || !this.IsAttachedToVisualTree() || DialogCard.Bounds.Height <= 0) return;
            _morphFrom = DialogCard.Bounds.Size;
            // 先预隐藏入场内容，等最终布局完成后与背景伸展在同一合成批次启动。
            _primedOpacity = DownloadPanel.SetValue(OpacityProperty, 0d, BindingPriority.Animation);
            DownloadPanel.IsEnabled = false;
            DialogCard.Focus();
        };
        DialogCard.LayoutUpdated += (_, _) =>
        {
            PresentOpeningAfterLayout();
            StartMorphAfterLayout();
        };
        AttachedToVisualTree += (_, _) =>
        {
            _topLevel = TopLevel.GetTopLevel(this);
            _topLevel?.AddHandler(InputElement.KeyDownEvent, OnModalKeyDown, RoutingStrategies.Tunnel);
            _topLevel?.AddHandler(InputElement.GotFocusEvent, OnModalGotFocus, RoutingStrategies.Bubble);
            if (IsVisible && IsEnabled && _openingCancellation is null) BeginOpeningPresentation();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            ResetOpeningPresentation();
            ResetMorph();
            _topLevel?.RemoveHandler(InputElement.KeyDownEvent, OnModalKeyDown);
            _topLevel?.RemoveHandler(InputElement.GotFocusEvent, OnModalGotFocus);
            _topLevel = null;
        };
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsEnabledProperty)
            {
                if (!IsEnabled)
                {
                    ResetOpeningPresentation();
                    ResetMorph();
                }
                else if (IsVisible) BeginOpeningPresentation();
            }
            if (e.Property != IsVisibleProperty) return;
            if (IsVisible)
            {
                _previousFocus = _topLevel?.FocusManager?.GetFocusedElement() as InputElement;
                BeginOpeningPresentation();
                Dispatcher.UIThread.Post(FocusChoice, DispatcherPriority.Loaded);
            }
            else
            {
                ResetOpeningPresentation();
                ResetMorph();
                if (_previousFocus is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true })
                    _previousFocus.Focus();
            }
        };
    }

    private void BeginOpeningPresentation()
    {
        ResetOpeningPresentation();
        ResetMorph();
        _openingCancellation = new CancellationTokenSource();
        // 元数据可能在 Refresh 中同步返回。展示层先保留查询卡片，不能依赖 VM 的异步时序。
        // 临时优先级值不会覆盖绑定；释放后仍由真实查询状态决定显示哪一块内容。
        _loadingVisibility = LoadingPanel.SetValue(IsVisibleProperty, true, BindingPriority.Animation);
        _downloadVisibility = DownloadPanel.SetValue(IsVisibleProperty, false, BindingPriority.Animation);
        DialogCard.InvalidateMeasure();
    }

    private void PresentOpeningAfterLayout()
    {
        if (_openingCancellation is not { } cancellation || _openingCommitted
            || !IsEffectivelyVisible || !IsEffectivelyEnabled || DialogCard.Bounds.Height <= 0) return;
        if (ElementComposition.GetElementVisual(DialogSurface) is not { } surface)
        {
            ResetOpeningPresentation();
            return;
        }
        _openingCommitted = true;
        _ = CompleteOpeningPresentationAsync(surface.Compositor, cancellation);
    }

    private async Task CompleteOpeningPresentationAsync(Compositor compositor, CancellationTokenSource cancellation)
    {
        try
        {
            // 从查询卡片的首个合成提交开始计时，避免弹窗挂载/淡入之前就耗完停留时间。
            await compositor.RequestCommitAsync().WaitAsync(cancellation.Token);
            await Task.Delay(QueryPresentationDuration, cancellation.Token);
            if (!ReferenceEquals(_openingCancellation, cancellation)) return;
            ResetOpeningPresentation();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally { cancellation.Dispose(); }
    }

    private void ResetOpeningPresentation()
    {
        var cancellation = _openingCancellation;
        _openingCancellation = null;
        cancellation?.Cancel();
        // 未进入异步方法的源也要释放；已经进入的由该方法的 finally 释放。
        if (!_openingCommitted) cancellation?.Dispose();
        _openingCommitted = false;
        _loadingVisibility?.Dispose();
        _loadingVisibility = null;
        _downloadVisibility?.Dispose();
        _downloadVisibility = null;
    }

    private void StartMorphAfterLayout()
    {
        if (_morphFrom is not { } from) return;
        var to = DialogCard.Bounds.Size;
        if (to.Width <= 0 || to.Height <= 0) return;
        var surface = ElementComposition.GetElementVisual(DialogSurface);
        var content = ElementComposition.GetElementVisual(DownloadPanel);
        if (surface is null || content is null || !IsEffectivelyVisible || !IsEffectivelyEnabled)
        {
            ResetMorph();
            FocusChoice();
            return;
        }
        _morphFrom = null;
        _primedOpacity?.Dispose();
        _primedOpacity = null;

        // 布局一次到最终尺寸，背景从旧尺寸反向缩放后还原；不逐帧改 Height/Measure。
        surface.CenterPoint = new Vector3D(to.Width / 2, to.Height / 2, 0);
        surface.Scale = new Vector3D(1, 1, 1);
        var stretch = surface.Compositor.CreateVector3DKeyFrameAnimation();
        stretch.Duration = MorphDuration;
        stretch.InsertKeyFrame(0f, new Vector3D(from.Width / to.Width, from.Height / to.Height, 1));
        stretch.InsertKeyFrame(1f, new Vector3D(1, 1, 1), MorphEasing);
        surface.StartAnimation("Scale", stretch);

        content.Opacity = 1;
        content.Translation = default;
        var fade = content.Compositor.CreateScalarKeyFrameAnimation();
        fade.Duration = MorphDuration;
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(0.5f, 0f);
        fade.InsertKeyFrame(1f, 1f, MorphEasing);
        content.StartAnimation("Opacity", fade);
        var slide = content.Compositor.CreateVector3DKeyFrameAnimation();
        slide.Duration = MorphDuration;
        slide.InsertKeyFrame(0f, new Vector3D(0, 8, 0));
        slide.InsertKeyFrame(0.5f, new Vector3D(0, 8, 0));
        slide.InsertKeyFrame(1f, default, MorphEasing);
        content.StartAnimation("Translation", slide);

        var cancellation = new CancellationTokenSource();
        _morphCancellation = cancellation;
        _ = CompleteMorphAsync(surface.Compositor, cancellation);
    }

    private async Task CompleteMorphAsync(Compositor compositor, CancellationTokenSource cancellation)
    {
        try
        {
            await compositor.RequestCommitAsync().WaitAsync(cancellation.Token);
            await Task.Delay(MorphDuration, cancellation.Token);
            if (!ReferenceEquals(_morphCancellation, cancellation)) return;
            ResetMorph();
            FocusChoice();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally { cancellation.Dispose(); }
    }

    private void ResetMorph()
    {
        _morphFrom = null;
        var cancellation = _morphCancellation;
        _morphCancellation = null;
        cancellation?.Cancel();
        _primedOpacity?.Dispose();
        _primedOpacity = null;
        if (ElementComposition.GetElementVisual(DialogSurface) is { } surface)
        {
            surface.StopAnimation("Scale");
            surface.Scale = new Vector3D(1, 1, 1);
        }
        if (ElementComposition.GetElementVisual(DownloadPanel) is { } content)
        {
            content.StopAnimation("Opacity");
            content.StopAnimation("Translation");
            content.Opacity = 1;
            content.Translation = default;
        }
        DownloadPanel.IsEnabled = true;
    }

    private void FocusChoice()
    {
        if (!IsVisible || !IsEnabled) return;
        if (IsMorphing)
        {
            DialogCard.Focus();
            return;
        }
        if (LoadingPanel.IsVisible)
        {
            LoadingCancelButton.Focus();
            return;
        }
        if (QualityList.IsEnabled && QualityList.ContainerFromIndex(QualityList.SelectedIndex)
            is InputElement item && item.Focus()) return;
        CloseButton.Focus();
    }

    private void OnModalKeyDown(object? sender, KeyEventArgs e)
    {
        if (!IsVisible || (DataContext as MainViewModel)?.IsErrorDialogOpen == true) return;
        if (e.Key == Key.Escape)
        {
            (DataContext as MainViewModel)?.DownloadSongDialog?.CloseCommand.Execute(null);
            e.Handled = true;
        }
        else if (!IsEnabled || e.Source is not Visual source || !this.IsVisualAncestorOf(source))
        {
            e.Handled = true;
            FocusChoice();
        }
    }

    private void OnModalGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (IsVisible && IsEnabled && (DataContext as MainViewModel)?.IsErrorDialogOpen != true
            && e.Source is Visual source && !this.IsVisualAncestorOf(source)) FocusChoice();
    }
}
