namespace ALyricEase.ViewModels;

/// <summary>Debug 页 VM(仅 DEBUG 构建从左下角入口进入)。页面本身无状态,
/// 各测试按钮直接用 $parent[Window] 调 MainViewModel 命令(与 SearchView chip 同一模式)。</summary>
public sealed class DebugViewModel : ViewModelBase
{
}
