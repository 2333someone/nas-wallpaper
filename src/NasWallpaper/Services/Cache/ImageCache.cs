using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;
using NasWallpaper.Models;
using NasWallpaper.Services.Sources;

namespace NasWallpaper.Services.Cache;

public sealed class ImageCache
{
    public const long MaximumImageBytes = 100L * 1024 * 1024;
    private const int BufferSize = 81920;
    private readonly string _root;

    public ImageCache(string root)
    {
        _root = ValidateRoot(root);
        Directory.CreateDirectory(_root);
    }

    public string Root => _root;

    public async Task<CachedImage?> EnsureAsync(
        IImageSource source, SourceFile file, WallpaperOrientation orientation,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(file);
        if (file.Length > MaximumImageBytes) return null;

        var key = CreateKey(file);
        var destination = Path.Combine(_root, key + ".cache");
        if (TryGetExisting(destination, key, file, ct, out var cached))
            return await Task.Run(() => IsUsableImage(destination, orientation), ct)
                .ConfigureAwait(false) ? cached : null;

        var temporary = Path.Combine(_root,
            key + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var input = await source.OpenReadAsync(file, ct).ConfigureAwait(false))
            await using (var output = new FileStream(temporary, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[BufferSize];
                long total = 0;
                while (true)
                {
                    var read = await input.ReadAsync(buffer, ct).ConfigureAwait(false);
                    if (read == 0) break;
                    total += read;
                    if (total > MaximumImageBytes) return null;
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
            }

            if (!await Task.Run(() => IsUsableImage(temporary, orientation), ct)
                .ConfigureAwait(false))
                return null;
            ct.ThrowIfCancellationRequested();

            if (File.Exists(destination)) File.Delete(temporary);
            else File.Move(temporary, destination);
            return TryGetExisting(destination, key, file, ct, out cached) ? cached : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsExpectedDownloadError(ex))
        {
            return null;
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    /// <summary>只读图片头判断方向，不下载整张图；用于直接使用 NAS 原图的场景。</summary>
    public static async Task<bool> MatchesOrientationAsync(
        IImageSource source, SourceFile file, WallpaperOrientation orientation, CancellationToken ct)
    {
        try
        {
            await using var stream = await source.OpenReadAsync(file, ct).ConfigureAwait(false);
            var decoder = BitmapDecoder.Create(stream,
                BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreImageCache,
                BitmapCacheOption.None);
            if (decoder.Frames.Count == 0) return false;
            var frame = decoder.Frames[0];
            if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0) return false;
            return orientation == WallpaperOrientation.Any || frame.PixelWidth >= frame.PixelHeight;
        }
        catch (Exception ex) when (IsExpectedDownloadError(ex))
        {
            return false;
        }
    }

    public static string CreateKey(SourceFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var identity = file.Location + "\0" + file.Length.ToString(
            System.Globalization.CultureInfo.InvariantCulture) + "\0" +
            file.LastWriteUtc.UtcDateTime.Ticks.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant();
    }

    public static int Cleanup(
        string root, TimeSpan ttl, long maximumBytes,
        IEnumerable<string>? protectedPaths = null, DateTimeOffset? now = null)
    {
        root = ValidateRoot(root);
        if (ttl < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ttl));
        if (maximumBytes < 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        var protectedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (protectedPaths is not null)
        {
            foreach (var path in protectedPaths)
                if (!string.IsNullOrWhiteSpace(path)) protectedSet.Add(Path.GetFullPath(path));
        }
        protectedSet.Add(root);
        if (!Directory.Exists(root)) return 0;

        var currentTime = now ?? DateTimeOffset.UtcNow;
        var files = new List<FileInfo>();
        var removed = 0;
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
        {
            var fullPath = Path.GetFullPath(path);
            if (protectedSet.Contains(fullPath)) continue;
            try
            {
                var info = new FileInfo(fullPath);
                if (!info.Exists) continue;
                if (info.Extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase))
                {
                    if (currentTime - Timestamp(info) >= TimeSpan.FromHours(24))
                    {
                        info.Delete();
                        removed++;
                    }
                }
                else if (info.Extension.Equals(".cache", StringComparison.OrdinalIgnoreCase))
                {
                    files.Add(info);
                }
            }
            catch (Exception ex) when (IsCleanupError(ex))
            {
            }
        }

        foreach (var info in files.Where(f => IsExpired(f, currentTime, ttl)))
        {
            if (Delete(info)) removed++;
        }
        files.RemoveAll(f => !f.Exists);
        long size = files.Sum(f => Length(f));
        foreach (var info in files.OrderBy(f => Timestamp(f)).ThenBy(f => f.FullName,
            StringComparer.OrdinalIgnoreCase))
        {
            if (size <= maximumBytes) break;
            var length = Length(info);
            if (!Delete(info)) continue;
            size -= length;
            removed++;
        }
        return removed;
    }

    private static bool TryGetExisting(
        string destination, string key, SourceFile source, CancellationToken ct,
        out CachedImage cached)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(destination))
        {
            cached = null!;
            return false;
        }
        var info = new FileInfo(destination);
        Touch(info);
        cached = new CachedImage(info.FullName, key, info.Length, source.LastWriteUtc,
            Path.GetFileName(source.Location));
        return true;
    }

    private static bool IsUsableImage(string path, WallpaperOrientation orientation)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 4096, FileOptions.SequentialScan);
        var decoder = BitmapDecoder.Create(stream,
            BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreImageCache,
            BitmapCacheOption.None);
        if (decoder.Frames.Count == 0) return false;
        var frame = decoder.Frames[0];
        if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0) return false;
        return orientation == WallpaperOrientation.Any || frame.PixelWidth >= frame.PixelHeight;
    }

    private static bool Delete(FileInfo file)
    {
        try
        {
            file.Refresh();
            if (!file.Exists) return false;
            file.Delete();
            return true;
        }
        catch (Exception ex) when (IsCleanupError(ex))
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (IsCleanupError(ex))
        {
        }
    }

    private static void Touch(FileInfo file)
    {
        try { file.LastAccessTimeUtc = DateTime.UtcNow; }
        catch (Exception ex) when (IsCleanupError(ex)) { }
    }

    private static bool IsExpired(FileInfo file, DateTimeOffset now, TimeSpan ttl)
    {
        var timestamp = Timestamp(file);
        return timestamp <= now - ttl;
    }

    private static DateTimeOffset Timestamp(FileInfo file)
    {
        file.Refresh();
        var access = new DateTimeOffset(file.LastAccessTimeUtc, TimeSpan.Zero);
        var write = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
        return access > write ? access : write;
    }

    private static long Length(FileInfo file)
    {
        file.Refresh();
        return file.Exists ? file.Length : 0;
    }

    private static string ValidateRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("缓存目录不能为空。", nameof(root));
        var fullPath = Path.GetFullPath(root);
        if (string.Equals(fullPath, Path.GetPathRoot(fullPath), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("缓存目录不能是文件系统根目录。", nameof(root));
        return fullPath;
    }

    private static bool IsExpectedDownloadError(Exception ex) =>
        ex is IOException or HttpRequestException or UnauthorizedAccessException or
            NotSupportedException or System.Security.SecurityException or FileFormatException;

    private static bool IsCleanupError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or
            System.Security.SecurityException or NotSupportedException;
}
