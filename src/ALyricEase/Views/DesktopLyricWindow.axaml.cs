using System.ComponentModel;
using ALyricEase.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace ALyricEase.Views;

public sealed partial class DesktopLyricWindow : Window
{
    private DesktopLyricViewModel? _model;
    private bool _opened;
    private bool _updatingToolbar;

    /// <summary>
    /// 这次关闭是不是用户主动关的(点 × / Alt+F4)。应用退出时 Avalonia 会连带关闭所有窗口,
    /// 那种关闭不能当成"用户不要歌词了"。
    /// </summary>
    internal bool ClosedByUser { get; private set; }

    public PixelPoint ExpandedPosition => Position;

    public DesktopLyricWindow()
    {
        InitializeComponent();
        Closing += OnWindowClosing;
        DataContextChanged += (_, _) =>
        {
            if (_model is not null) _model.PropertyChanged -= OnModelChanged;
            _model = DataContext as DesktopLyricViewModel;
            if (_model is not null) _model.PropertyChanged += OnModelChanged;
            UpdateToolbarLayout();
        };
        Opened += (_, _) => { _opened = true; UpdateToolbarLayout(); };
        LayoutUpdated += (_, _) => UpdateToolbarLayout();
        Closed += (_, _) =>
        {
            _opened = false;
            if (_model is not null) _model.PropertyChanged -= OnModelChanged;
        };
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
        => ClosedByUser = e.CloseReason == WindowCloseReason.WindowClosing;

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DesktopLyricViewModel.ClickThrough)) UpdateToolbarLayout();
    }

    private void UpdateToolbarLayout()
    {
        if (!_opened || _updatingToolbar) return;
        _updatingToolbar = true;
        try
        {
            var collapse = _model?.ClickThrough == true;
            // 同一渲染帧内只修改透明度和背景绘制范围。保留透明的原生窗口上沿，
            // 避免移动旧帧缓冲、SizeToContent 缩放以及新帧提交之间出现闪烁。
            Toolbar.Opacity = collapse ? 0 : 1;
            LyricSurface.Margin = new Thickness(0,
                collapse ? Toolbar.Bounds.Height + LyricLayout.RowSpacing : 0, 0, 0);
        }
        finally { _updatingToolbar = false; }
    }

    private void OnDragPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }
}
