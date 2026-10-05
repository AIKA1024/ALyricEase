using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ALyricEase.ViewModels;

public sealed record CloseBehaviorChoice(bool MinimizeToTray, bool Remember);

public sealed partial class CloseBehaviorDialogViewModel : ViewModelBase
{
    [ObservableProperty] private bool _rememberChoice;

    public event Action<CloseBehaviorChoice?>? CompletionRequested;

    [RelayCommand]
    private void MinimizeToTray() => CompletionRequested?.Invoke(new(true, RememberChoice));

    [RelayCommand]
    private void Exit() => CompletionRequested?.Invoke(new(false, RememberChoice));

    [RelayCommand]
    private void Cancel() => CompletionRequested?.Invoke(null);
}
