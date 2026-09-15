using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.Services.NetEase;
using ALyricEase.Services.QQMusic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ALyricEase.ViewModels;

public sealed partial class AccountPlatformViewModel : ViewModelBase
{
    public AccountPlatformViewModel(string platformName) => PlatformName = platformName;

    public string PlatformName { get; }

    [ObservableProperty] private bool _isLoggedIn;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _userIdText = "—";
    [ObservableProperty] private string _membershipName = "普通用户";
    [ObservableProperty] private string _membershipLevelText = "未开通";
    [ObservableProperty] private string _accountLevelText = "暂未提供";
    [ObservableProperty] private string _membershipDetail = "暂无会员权益";
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string _avatarUrl = "";

    public string AvatarLetter => string.IsNullOrWhiteSpace(Username) ? PlatformName[..1] : Username[..1];
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    partial void OnUsernameChanged(string value) => OnPropertyChanged(nameof(AvatarLetter));
    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(HasError));

    public void SetLoginState(bool loggedIn, string fallbackName = "")
    {
        IsLoggedIn = loggedIn;
        if (loggedIn)
        {
            if (Username.Length == 0) Username = fallbackName;
            return;
        }

        IsLoading = false;
        Username = "";
        UserIdText = "—";
        MembershipName = "普通用户";
        MembershipLevelText = "未开通";
        AccountLevelText = "暂未提供";
        MembershipDetail = "暂无会员权益";
        ErrorMessage = null;
        AvatarUrl = "";
    }

    public async Task LoadAsync(Func<CancellationToken, Task<MusicAccountSummary>> loader, CancellationToken ct)
    {
        if (!IsLoggedIn) return;
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var summary = await loader(ct);
            ct.ThrowIfCancellationRequested();
            Username = summary.Nickname.Length > 0 ? summary.Nickname : PlatformName + "用户";
            UserIdText = summary.UserId > 0 ? summary.UserId.ToString() : "—";
            MembershipName = summary.MembershipName;
            MembershipLevelText = summary.IsVip
                ? summary.MembershipLevel > 0 ? $"LV {summary.MembershipLevel}" : "已开通"
                : "未开通";
            AccountLevelText = summary.AccountLevel > 0 ? $"LV {summary.AccountLevel}" : "暂未提供";
            MembershipDetail = summary.MembershipExpiresAt is { } expires
                ? $"有效期至 {expires.ToLocalTime():yyyy-MM-dd}"
                : summary.IsVip ? "会员权益已生效" : "暂无会员权益";
            AvatarUrl = summary.AvatarUrl;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (ApiException ex)
        {
            ErrorMessage = $"账号信息暂时无法刷新：{ex.Message}";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"网络连接失败：{ex.Message}";
        }
        finally
        {
            if (!ct.IsCancellationRequested) IsLoading = false;
        }
    }
}

public sealed partial class AccountViewModel : ViewModelBase
{
    private readonly NetEaseApiClient _netEase;
    private readonly QQMusicApiClient _qq;
    private readonly PlaylistViewModel _playlist;
    private CancellationTokenSource? _refreshCancellation;

    public AccountViewModel(NetEaseApiClient netEase, QQMusicApiClient qq, PlaylistViewModel playlist)
    {
        _netEase = netEase;
        _qq = qq;
        _playlist = playlist;
        NetEase = new AccountPlatformViewModel("网易云音乐");
        Qq = new AccountPlatformViewModel("QQ 音乐");
        NetEase.PropertyChanged += OnPlatformPropertyChanged;
        Qq.PropertyChanged += OnPlatformPropertyChanged;
        SyncLoginState();
    }

    public AccountPlatformViewModel NetEase { get; }
    public AccountPlatformViewModel Qq { get; }

    public bool HasAnyLogin => NetEase.IsLoggedIn || Qq.IsLoggedIn;
    public bool IsRefreshing => NetEase.IsLoading || Qq.IsLoading;

    public void SyncLoginState()
    {
        NetEase.SetLoginState(_playlist.IsLoggedIn, _playlist.UserName);
        Qq.SetLoginState(_playlist.IsQqLoggedIn, _playlist.QqUserName);
        NotifyPageState();
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _refreshCancellation = cancellation;
        SyncLoginState();
        try
        {
            await Task.WhenAll(
                NetEase.LoadAsync(_netEase.GetAccountSummaryAsync, cancellation.Token),
                Qq.LoadAsync(_qq.GetAccountSummaryAsync, cancellation.Token));
        }
        finally
        {
            if (ReferenceEquals(_refreshCancellation, cancellation))
            {
                _refreshCancellation = null;
                NotifyPageState();
            }
            cancellation.Dispose();
        }
    }

    [RelayCommand]
    private void LogoutNetEase()
    {
        _playlist.LogoutNetEase();
        SyncLoginState();
    }

    [RelayCommand]
    private void LogoutQq()
    {
        _playlist.LogoutQq();
        SyncLoginState();
    }

    /// <summary>未登录平台的占位卡片"登录"按钮:打开登录弹层并定位到该平台标签
    /// (与红心等账号操作发现未登录时的定位走同一条 OpenLoginDialogFor)。
    /// 登录成功后 MainViewModel.OnPlaylistLoginChanged 会 SyncLoginState + RefreshAsync,
    /// 占位卡片自动换成真实账号卡片。</summary>
    [RelayCommand]
    private void LoginNetEase()
        => ServiceLocator.Get<MainViewModel>().OpenLoginDialogFor(MusicSource.NetEase);

    [RelayCommand]
    private void LoginQq()
        => ServiceLocator.Get<MainViewModel>().OpenLoginDialogFor(MusicSource.QQ);

    private void NotifyPageState()
    {
        OnPropertyChanged(nameof(HasAnyLogin));
        OnPropertyChanged(nameof(IsRefreshing));
    }

    private void OnPlatformPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AccountPlatformViewModel.IsLoading) or
            nameof(AccountPlatformViewModel.IsLoggedIn))
            NotifyPageState();
    }
}
