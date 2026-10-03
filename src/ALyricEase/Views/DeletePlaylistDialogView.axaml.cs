using Avalonia.Controls;

namespace ALyricEase.Views;

/// <summary>删除歌单确认对话框视图；显隐动画由宿主统一协调，无输入框、不抢焦点。</summary>
public partial class DeletePlaylistDialogView : UserControl
{
    public DeletePlaylistDialogView()
    {
        InitializeComponent();
    }
}
