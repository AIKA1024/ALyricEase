using ALyricEase.Services.NetEase;
using ALyricEase.Services.QQMusic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ALyricEase.ViewModels;

public sealed partial class MainViewModel
{
    [ObservableProperty] private bool _isErrorDialogOpen;
    [ObservableProperty] private string _errorDialogTitle = "";
    [ObservableProperty] private string _errorDialogMessage = "";
    private bool _returnFromFailedQqUser;

    private void OnQqUserLoadFailed(Exception error)
    {
        if (ActivePage != "User") return;

        ErrorDialogTitle = "无法打开 QQ 音乐用户页";
        ErrorDialogMessage = error switch
        {
            ApiException ex when QQMusicApiClient.RequiresFreshLogin(ex.Code)
                => $"QQ 音乐登录凭证需要更新，请重新登录。\n\n原因：{ex.Message}",
            HttpRequestException ex => $"连接 QQ 音乐失败，请检查网络后重试。\n\n原因：{ex.Message}",
            OperationCanceledException => "连接 QQ 音乐超时，请稍后重试。",
            _ => error.Message,
        };
        _returnFromFailedQqUser = true;
        IsErrorDialogOpen = true;
    }

    [RelayCommand]
    private void CloseErrorDialog()
    {
        if (!IsErrorDialogOpen) return;
        IsErrorDialogOpen = false;
        var shouldReturn = _returnFromFailedQqUser;
        _returnFromFailedQqUser = false;
        if (!shouldReturn || ActivePage != "User") return;

        if (CanGoBack)
            GoBack();
        else
        {
            // 用户确认关闭后才离开失败页；启动无历史时回到账户页，不留下空白页历史。
            _isGoingBack = true;
            try { ActivePage = "Account"; }
            finally { _isGoingBack = false; }
        }
    }
}
