using System.IO;
using System.Runtime.CompilerServices;
using NasWallpaper.Models;
using NasWallpaper.Services.Cache;

namespace NasWallpaper.Services.Sources;

internal sealed class FileImageSource : IImageSource
{
    private const int ScanLimit = 10_000;
    private readonly string _root;
    private readonly bool _recursive;

    public FileImageSource(SourceConfig config)
    {
        _root = Path.GetFullPath(config.Address.Trim());
        _recursive = config.Recursive;
        if (config.Kind == SourceKind.Smb)
            SmbConnection.EnsureConnected(_root, config.Username,
                Configuration.CredentialProtector.Unprotect(config.EncryptedPassword));
    }

    public async IAsyncEnumerable<SourceFile> ScanAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        var files = await Task.Run(() => Scan(ct), ct).ConfigureAwait(false);
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            yield return file;
        }
    }

    public async Task<Stream> OpenReadAsync(SourceFile file, CancellationToken ct)
    {
        var path = Path.GetFullPath(file.Location);
        return await Task.Run<Stream>(() => new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan), ct)
            .ConfigureAwait(false);
    }

    public async Task DeleteAsync(string location, CancellationToken ct)
    {
        var path = Path.GetFullPath(location);
        await Task.Run(() => File.Delete(path), ct).ConfigureAwait(false);
    }

    private List<SourceFile> Scan(CancellationToken ct)
    {
        if (!Directory.Exists(_root))
            throw new IOException($"无法访问图片目录：{_root}");

        var files = new List<SourceFile>();
        var pending = new Stack<string>();
        pending.Push(_root);

        while (pending.Count > 0 && files.Count < ScanLimit)
        {
            ct.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            string[] paths;
            try
            {
                paths = Directory.GetFiles(directory);
            }
            catch (Exception ex) when (SourceErrors.IsExpected(ex))
            {
                continue;
            }

            foreach (var path in paths)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var info = new FileInfo(path);
                    if (!info.Exists || !IImageSource.IsImagePath(info.FullName) ||
                        info.Length == 0 || info.Length > ImageCache.MaximumImageBytes) continue;
                    files.Add(new SourceFile(
                        info.FullName, info.Length, info.LastWriteTimeUtc));
                    if (files.Count >= ScanLimit) break;
                }
                catch (Exception ex) when (SourceErrors.IsExpected(ex))
                {
                }
            }

            if (!_recursive) continue;
            try
            {
                foreach (var child in Directory.GetDirectories(directory))
                    pending.Push(child);
            }
            catch (Exception ex) when (SourceErrors.IsExpected(ex))
            {
            }
        }

        return files;
    }
}

internal static class SourceErrors
{
    public static bool IsExpected(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or
            System.Security.SecurityException or NotSupportedException;
}
