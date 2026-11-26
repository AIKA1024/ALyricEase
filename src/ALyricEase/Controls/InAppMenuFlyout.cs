using Avalonia.Controls;

namespace ALyricEase.Controls;

public sealed class InAppMenuFlyout : MenuFlyout
{
    protected override Control CreatePresenter()
    {
        // 与页面共享合成目标，菜单的 BackdropEffect 才能采样下方内容。
        Popup.ShouldUseOverlayLayer = true;
        return base.CreatePresenter();
    }
}
