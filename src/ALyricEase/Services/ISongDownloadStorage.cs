namespace ALyricEase.Services;

/// <summary>平台下载目录。仅完整写入后发布文件；失败或取消时移除临时结果。</summary>
public interface ISongDownloadStorage
{
    Task<string> SaveAsync(string fileName, string mimeType,
        Func<Stream, CancellationToken, Task> write, CancellationToken cancellationToken);
}

/// <summary>文件系统下载目录的原子保存，同名文件自动编号。</summary>
public sealed class DirectorySongDownloadStorage(Func<string> getDirectory) : ISongDownloadStorage
{
    public async Task<string> SaveAsync(string fileName, string mimeType,
        Func<Stream, CancellationToken, Task> write, CancellationToken cancellationToken)
    {
        var directory = getDirectory();
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".alyricease-{Guid.NewGuid():N}.part");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 81920, useAsync: true))
            {
                await write(stream, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            for (var index = 0; ; index++)
            {
                var name = index == 0 ? fileName
                    : $"{Path.GetFileNameWithoutExtension(fileName)} ({index}){Path.GetExtension(fileName)}";
                var destination = Path.Combine(directory, name);
                try
                {
                    File.Move(temporary, destination, overwrite: false);
                    return destination;
                }
                catch (IOException) when (File.Exists(destination)) { }
            }
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
