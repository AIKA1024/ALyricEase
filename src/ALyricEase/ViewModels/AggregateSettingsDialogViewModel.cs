using System.IO;
using ALyricEase.Models;
using ALyricEase.Services;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ALyricEase.ViewModels;

/// <summary>聚合歌单设置对话框 VM:成员按来源的排列顺序 + 自定义封面。
/// 封面选图即时预览、保存时才落位(取消不生效);落位文件在应用数据目录 covers/ 下,模型只存文件名。</summary>
public sealed partial class AggregateSettingsDialogViewModel : ViewModelBase
{
    private readonly Action<AggregatePlaylist> _onSaved;
    private AggregatePlaylist? _current;

    /// <summary>本次刚选的封面(临时文件,保存时才复制进 covers/);null = 没选新图。</summary>
    private string? _pendingCoverTempPath;

    /// <summary>用户点了"恢复默认",保存时删除自定义封面文件并清空引用。</summary>
    private bool _pendingCoverClear;

    public AggregateSettingsDialogViewModel(Action<AggregatePlaylist> onSaved)
    {
        _onSaved = onSaved;
    }

    /// <summary>当前聚合歌单名(标题副文案)。</summary>
    [ObservableProperty] private string _aggregateName = "";

    /// <summary>单选:网易云在前。</summary>
    [ObservableProperty] private bool _netEaseFirst;

    /// <summary>单选:QQ在前。</summary>
    [ObservableProperty] private bool _qqFirst;

    /// <summary>自定义封面预览(本地解码后的位图;null = 无自定义封面)。</summary>
    [ObservableProperty] private IImage? _customCoverPreview;

    /// <summary>存在自定义封面(现用或待存),控制"恢复默认"按钮可用性。</summary>
    [ObservableProperty] private bool _hasCustomCover;

    /// <summary>打开对话框时调用:按当前设置初始化。</summary>
    public void Refresh(AggregatePlaylist aggregate)
    {
        _current = aggregate;
        AggregateName = aggregate.Name;
        NetEaseFirst = aggregate.SourceOrder != AggregateSourceOrder.QqFirst;
        QqFirst = aggregate.SourceOrder == AggregateSourceOrder.QqFirst;
        _pendingCoverTempPath = null;
        _pendingCoverClear = false;
        var path = AggregateCoverStore.ResolvePath(aggregate.CustomCover);
        CustomCoverPreview = LoadPreview(path);
        HasCustomCover = path is not null;
    }

    /// <summary>文件选择回调(视图层完成 Picker 后调用):解码、长边压到 1024、存临时文件并即时预览。</summary>
    public void ApplyPickedCover(Stream imageStream)
    {
        try
        {
            using var imageStream0 = imageStream;
            using var decoded = new Bitmap(imageStream);
            // 封面展示最大 400px 解码,原图长边压到 1024:省磁盘也省解码,肉眼无损
            var scale = Math.Min(1.0, 1024.0 / Math.Max(decoded.PixelSize.Width, decoded.PixelSize.Height));
            using var scaled = scale < 1.0
                ? decoded.CreateScaledBitmap(new Avalonia.PixelSize(
                    Math.Max(1, (int)(decoded.PixelSize.Width * scale)),
                    Math.Max(1, (int)(decoded.PixelSize.Height * scale))))
                : null;
            var source = scaled ?? decoded;
            var temp = Path.Combine(Path.GetTempPath(), $"aly-aggregate-cover-{Guid.NewGuid():N}.png");
            using (var file = File.Create(temp)) source.Save(file);
            TryDelete(_pendingCoverTempPath);
            _pendingCoverTempPath = temp;
            _pendingCoverClear = false;
            CustomCoverPreview = LoadPreview(temp);
            HasCustomCover = true;
        }
        catch
        {
            // 选中的文件不是有效图片:静默忽略,保持原状态
        }
    }

    /// <summary>恢复默认:清掉待存/现用封面(保存时生效)。</summary>
    [RelayCommand]
    private void ClearCustomCover()
    {
        TryDelete(_pendingCoverTempPath);
        _pendingCoverTempPath = null;
        _pendingCoverClear = true;
        CustomCoverPreview = null;
        HasCustomCover = false;
    }

    [RelayCommand]
    private void Save()
    {
        if (_current is null) return;
        _current.SourceOrder = QqFirst ? AggregateSourceOrder.QqFirst : AggregateSourceOrder.NetEaseFirst;
        CommitCover();
        _onSaved(_current);
    }

    private void CommitCover()
    {
        if (_current is null) return;
        if (_pendingCoverClear)
        {
            AggregateCoverStore.Delete(_current.Id);
            _current.CustomCover = null;
        }
        else if (_pendingCoverTempPath is not null)
        {
            try
            {
                _current.CustomCover = AggregateCoverStore.Save(_current.Id, _pendingCoverTempPath);
            }
            catch
            {
                // 落位失败(磁盘满/权限):保留原自定义封面不动
            }
            finally
            {
                TryDelete(_pendingCoverTempPath);
            }
        }
        _pendingCoverTempPath = null;
        _pendingCoverClear = false;
    }

    private static IImage? LoadPreview(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try { return new Bitmap(path); }
        catch { return null; }
    }

    private static void TryDelete(string? path)
    {
        try { if (path is not null) File.Delete(path); }
        catch { /* 临时文件删不掉无碍 */ }
    }
}
