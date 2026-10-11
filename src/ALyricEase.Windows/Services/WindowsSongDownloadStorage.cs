using System.Runtime.InteropServices;

namespace ALyricEase.Services;

/// <summary>读取 Known Folder，兼容用户迁移或重定向后的“下载”目录。</summary>
public sealed partial class WindowsSongDownloadStorage : ISongDownloadStorage
{
    private readonly DirectorySongDownloadStorage _storage = new(GetDownloadsDirectory);

    public Task<string> SaveAsync(string fileName, string mimeType,
        Func<Stream, CancellationToken, Task> write, CancellationToken cancellationToken)
        => _storage.SaveAsync(fileName, mimeType, write, cancellationToken);

    private static string GetDownloadsDirectory()
    {
        var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        var result = SHGetKnownFolderPath(in id, 0, 0, out var path);
        try
        {
            Marshal.ThrowExceptionForHR(result);
            return Marshal.PtrToStringUni(path)
                ?? throw new IOException("无法获取系统下载目录。");
        }
        finally { Marshal.FreeCoTaskMem(path); }
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHGetKnownFolderPath(in Guid id, uint flags, nint token, out nint path);
}
