using Android.Content;
using Android.Provider;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace ALyricEase.Services;

public sealed class AndroidSongDownloadStorage : ISongDownloadStorage
{
    public async Task<string> SaveAsync(string fileName, string mimeType,
        Func<Stream, CancellationToken, Task> write, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(29))
            return await SaveWithPickerAsync(fileName, write, cancellationToken).ConfigureAwait(false);

        var resolver = global::Android.App.Application.Context.ContentResolver
            ?? throw new IOException("无法访问系统下载目录。");
        using var values = new ContentValues();
        values.Put(MediaStore.IMediaColumns.DisplayName, fileName);
        values.Put(MediaStore.IMediaColumns.MimeType, mimeType);
        values.Put(MediaStore.IMediaColumns.RelativePath, global::Android.OS.Environment.DirectoryDownloads);
        values.Put(MediaStore.IMediaColumns.IsPending, 1);
        var uri = resolver.Insert(MediaStore.Downloads.ExternalContentUri!, values)
            ?? throw new IOException("无法在下载目录创建文件。");
        try
        {
            using (var output = resolver.OpenOutputStream(uri)
                ?? throw new IOException("无法写入下载文件。"))
            {
                await write(output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            using var completed = new ContentValues();
            completed.Put(MediaStore.IMediaColumns.IsPending, 0);
            if (resolver.Update(uri, completed, null, null) == 0)
                throw new IOException("无法完成下载文件保存。");
            // MediaStore 会自动处理重名；查询系统最终采用的文件名。
            using var cursor = resolver.Query(uri, [MediaStore.IMediaColumns.DisplayName], null, null, null);
            var actualName = cursor?.MoveToFirst() == true ? cursor.GetString(0) : fileName;
            return $"下载/{actualName}";
        }
        catch
        {
            resolver.Delete(uri, null, null);
            throw;
        }
    }

    // Android 6–9 使用系统文件选择器授予目标文件访问权，不要求整个存储的写入权限。
    private static async Task<string> SaveWithPickerAsync(string fileName,
        Func<Stream, CancellationToken, Task> write, CancellationToken cancellationToken)
    {
        var file = await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var lifetime = Avalonia.Application.Current?.ApplicationLifetime as ISingleViewApplicationLifetime;
            var storage = lifetime?.MainView is { } view ? TopLevel.GetTopLevel(view)?.StorageProvider : null;
            if (storage is null) throw new IOException("无法打开系统保存窗口。");
            return await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "保存歌曲", SuggestedFileName = fileName,
                SuggestedStartLocation = await storage.TryGetWellKnownFolderAsync(WellKnownFolder.Downloads),
            });
        });
        if (file is null) throw new OperationCanceledException(cancellationToken);
        using (file)
        {
            try
            {
                await using (var stream = await file.OpenWriteAsync())
                {
                    await write(stream, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                return file.Name;
            }
            catch
            {
                await file.DeleteAsync();
                throw;
            }
        }
    }
}
