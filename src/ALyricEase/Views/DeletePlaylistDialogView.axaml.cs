using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace ALyricEase.Views;

/// <summary>删除歌单确认对话框视图:打开(IsVisible=true)时先置 0 下一帧再置 1 触发淡入
/// (IsVisible 翻转本身不跑过渡)。无输入框,不抢焦点。与 RenamePlaylistDialogView 同款行为。</summary>
public partial class DeletePlaylistDialogView : UserControl
{
    public DeletePlaylistDialogView()
    {
        InitializeComponent();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && IsVisible)
            {
                Root.Opacity = 0;
                Dispatcher.UIThread.Post(() => Root.Opacity = 1, DispatcherPriority.Render);
            }
        };
    }
}
