using ALyricEase.ViewModels;
using Avalonia.Controls;
using Avalonia.Threading;

namespace ALyricEase.Views;

/// <summary>复用项目窗口内对话框设计；宿主负责模态输入约束与统一显隐动画。</summary>
public partial class CloseBehaviorDialogView : UserControl
{
    private readonly TaskCompletionSource<CloseBehaviorChoice?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CloseBehaviorDialogViewModel ViewModel { get; } = new();
    public Task<CloseBehaviorChoice?> Completion => _completion.Task;

    public CloseBehaviorDialogView()
    {
        InitializeComponent();
        DataContext = ViewModel;
        ViewModel.CompletionRequested += OnCompletionRequested;
        PropertyChanged += (_, e) =>
        {
            if (e.Property != IsVisibleProperty || !IsVisible) return;
            Dispatcher.UIThread.Post(FocusDefaultAction, DispatcherPriority.Loaded);
        };
    }

    public void FocusDefaultAction()
    {
        if (IsVisible && IsEnabled) MinimizeButton.Focus();
    }

    private void OnCompletionRequested(CloseBehaviorChoice? choice) => _completion.TrySetResult(choice);
}
