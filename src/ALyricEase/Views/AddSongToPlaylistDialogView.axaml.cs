using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace ALyricEase.Views;

/// <summary>添加到歌单模态视图：打开时淡入并把输入焦点放到搜索框。</summary>
public partial class AddSongToPlaylistDialogView : UserControl
{
    public AddSongToPlaylistDialogView()
    {
        InitializeComponent();
        SizeChanged += (_, e) => ApplyResponsiveSize(e.NewSize);
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && IsVisible)
            {
                Root.Opacity = 0;
                Dispatcher.UIThread.Post(() =>
                {
                    Root.Opacity = 1;
                    SearchBox.Focus();
                }, DispatcherPriority.Render);
            }
        };
    }

    private void ApplyResponsiveSize(Size size)
    {
        DialogCard.Width = Math.Max(300, Math.Min(440, size.Width - 32));
        DialogCard.MaxHeight = Math.Max(360, Math.Min(600, size.Height - 32));
    }
}
