using NasWallpaper.Models;
using NasWallpaper.Services.Cache;
using NasWallpaper.Services.Sources;
using System.IO;
using System.Net.Http;
using System.Windows.Media.Imaging;

namespace NasWallpaper.Services.Preview;

/// <summary>
/// 壁纸库缩略图加载器：为一台显示器的图片源复用同一条连接（WebDAV 只建一个 HttpClient），
/// 生成的缩略图写进现有缓存目录（thumb-*.cache），交给原有清理策略按容量与时间回收。
/// </summary>
public sealed class GalleryThumbnailLoader : IDisposable
{
    private const int JpegQuality = 82;
    private readonly IImageSource _source;
    private readonly ImageCache _cache;
    private readonly bool _direct;
    private readonly SemaphoreSlim _gate = new(2, 2);
    private bool _disposed;

    public GalleryThumbnailLoader(SourceConfig config, ImageCache cache)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(cache);
        _source = ImageSourceFactory.Create(config);
        _cache = cache;
        _direct = config.Kind is SourceKind.Smb or SourceKind.Local;
    }

    /// <summary>加载指定宽度的缩略图；失败返回 null，由界面显示占位图标。</summary>
    public async Task<BitmapSource?> LoadAsync(SourceFile file, int width, CancellationToken ct)
    {
        if (_disposed) return null;
        if (width < 64) width = 320;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed || ct.IsCancellationRequested) return null;

            var thumbnailPath = ThumbnailPath(file, width);
            if (File.Exists(thumbnailPath))
            {
                var cached = await Task.Run(() => TryDecode(thumbnailPath, width), ct).ConfigureAwait(false);
                if (cached is not null) return cached;
                TryDelete(thumbnailPath);
            }

            var original = await ResolveOriginalAsync(file, ct).ConfigureAwait(false);
            if (original is null) return null;

            return await Task.Run(() => CreateThumbnail(original, thumbnailPath, width), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            HttpRequestException or NotSupportedException or System.Security.SecurityException or
            FileFormatException or ObjectDisposedException or ArgumentException)
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>本地 / SMB 直接读原图；WebDAV 只能先下载到缓存再用缓存副本。</summary>
    private async Task<string?> ResolveOriginalAsync(SourceFile file, CancellationToken ct)
    {
        if (_direct && File.Exists(file.Location)) return file.Location;

        var cached = await _cache.EnsureAsync(_source, file, WallpaperOrientation.Any, ct)
            .ConfigureAwait(false);
        return cached?.Path;
    }

    private string ThumbnailPath(SourceFile file, int width) =>
        Path.Combine(_cache.Root, $"thumb-{ImageCache.CreateKey(file)}-{width}.cache");

    private static BitmapSource? CreateThumbnail(string originalPath, string thumbnailPath, int width)
    {
        var bitmap = TryDecode(originalPath, width);
        if (bitmap is null) return null;

        var temporary = thumbnailPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var encoder = new JpegBitmapEncoder { QualityLevel = JpegQuality };
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var output = File.Create(temporary)) encoder.Save(output);
            File.Move(temporary, thumbnailPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            System.Security.SecurityException or FileFormatException)
        {
            // 缩略图写不进缓存不影响浏览，下次再试。
        }
        finally
        {
            TryDelete(temporary);
        }
        return bitmap;
    }

    /// <summary>在后台线程解码并冻结，回到界面线程可以直接绑定。</summary>
    private static BitmapSource? TryDecode(string path, int width)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.SequentialScan);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.StreamSource = stream;
            if (width > 0) image.DecodePixelWidth = width;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            NotSupportedException or FileFormatException or ArgumentException)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            System.Security.SecurityException)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        (_source as IDisposable)?.Dispose();
    }
}
