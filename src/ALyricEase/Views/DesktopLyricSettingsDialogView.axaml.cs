using ALyricEase.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace ALyricEase.Views;

public partial class DesktopLyricSettingsDialogView : UserControl
{
    /// <summary>卡片高度上限:和 Fluent 2 ContentDialog 一样,再高就该内部滚动了。</summary>
    private const double MaxDialogHeight = 700;

    /// <summary>可用高度低到这个程度以下就不再压,宁可溢出也不能把内容区挤成 0。</summary>
    private const double MinDialogHeight = 280;

    private IInputElement? _previousFocus;

    public DesktopLyricSettingsDialogView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        // 卡片高度只能由外壳尺寸算:三个分段的内容高矮差很多(668/700/348),
        // 交给内容自适应的话点一次标签整张弹窗就跟着抽一下。
        SizeChanged += (_, _) => UpdateDialogHeight();
    }

    private void UpdateDialogHeight()
    {
        var available = Bounds.Height - DialogCard.Margin.Top - DialogCard.Margin.Bottom;
        if (!double.IsFinite(available) || available <= 0) return;
        var height = Math.Clamp(available, MinDialogHeight, MaxDialogHeight);
        // ⚠ Height 初始是 NaN,`Math.Abs(NaN - height) > 0.5` 恒为 false ⇒ 必须显式判 NaN,
        // 否则这里一次都不会写,卡片继续随内容伸缩。
        if (!double.IsFinite(DialogCard.Height) || Math.Abs(DialogCard.Height - height) > 0.5)
            DialogCard.Height = height;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != IsVisibleProperty) return;
        if (IsVisible)
        {
            _previousFocus = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
            Dispatcher.UIThread.Post(() => { if (IsVisible) DoneButton.Focus(); });
        }
        else
        {
            _previousFocus?.Focus();
            _previousFocus = null;
        }
    }

    /// <summary>
    /// 分段标签:只切可见性,不重建内容 —— 三个面板都是常驻视图,
    /// 滑块拖到一半切走再切回来,值和滚动位置都还在。
    /// </summary>
    private void OnTabClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string tag || !int.TryParse(tag, out var index)) return;
        SelectTab(index);
    }

    private void SelectTab(int index)
    {
        var tabs = new[] { TabGeneral, TabText, TabEffects };
        var panels = new Control[] { PanelGeneral, PanelText, PanelEffects };
        for (var i = 0; i < tabs.Length; i++)
        {
            tabs[i].Classes.Set("selected", i == index);
            panels[i].IsVisible = i == index;
        }
        // 三个段共用同一个滚动容器:不归零的话,在"常规"滚到底再切"效果"会直接落在半截上。
        ContentScroll.Offset = default;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DataContext is not MainViewModel model) return;
        model.CloseDesktopLyricDialogCommand.Execute(null);
        e.Handled = true;
    }
}
