using System.IO;
using ALyricEase.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace ALyricEase.Views;

/// <summary>聚合歌单设置对话框视图；显隐动画由宿主统一协调。
/// 自定义封面的文件选择在这里完成(需要 TopLevel/StorageProvider),结果交给 VM 预览与落位。</summary>
public partial class AggregateSettingsDialogView : UserControl
{
    public AggregateSettingsDialogView()
    {
        InitializeComponent();
    }

    private async void OnPickCoverClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;
        if (DataContext is not MainViewModel main) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择封面图片",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("图片文件")
                {
                    Patterns = ["*.jpg", "*.jpeg", "*.png", "*.webp", "*.bmp"],
                },
            ],
        });
        if (files.Count == 0) return;

        var stream = await files[0].OpenReadAsync();
        main.AggregateSettingsDialog.ApplyPickedCover(stream);
    }
}
