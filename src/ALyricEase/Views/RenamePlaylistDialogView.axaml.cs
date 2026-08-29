using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace ALyricEase.Views;

/// <summary>重命名歌单对话框视图:打开(IsVisible=true)时先置 0 下一帧再置 1 触发淡入,
/// 并聚焦名称输入框(IsVisible 翻转本身不跑过渡)。与 CreatePlaylistDialogView 同款行为。</summary>
public partial class RenamePlaylistDialogView : UserControl
{
    public RenamePlaylistDialogView()
    {
        InitializeComponent();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && IsVisible)
            {
                Root.Opacity = 0;
                Dispatcher.UIThread.Post(() =>
                {
                    Root.Opacity = 1;
                    NameBox.Focus();
                }, DispatcherPriority.Render);
            }
        };
    }
}
