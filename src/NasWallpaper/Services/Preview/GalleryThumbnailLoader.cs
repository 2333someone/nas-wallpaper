using NasWallpaper.Models;
using NasWallpaper.Services.Cache;
using NasWallpaper.Services.Sources;
using System.IO;
using System.Net.Http;
using System.Windows.Media.Imaging;

namespace NasWallpaper.Services.Preview;

/// <summary>预览图：解码结果 + 原图真实分辨率（用于界面显示，避免把小图放大后的尺寸当成原图尺寸）。</summary>
public sealed record PreviewImage(BitmapSource Image, int Width, int Height);

/// <summary>
/// 壁纸库缩略图加载器：为一台显示器的图片源复用同一条连接（WebDAV 只建一个 HttpClient），
/// 生成的缩略图写进现有缓存目录（thumb-*.cache），交给原有清理策略按容量与时间回收。
/// 缓存文件名里带上原图尺寸，命中缓存时无需再读原图。
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

    /// <summary>加载指定宽度的预览图；失败返回 null，由界面显示占位图标。</summary>
    public async Task<PreviewImage?> LoadAsync(SourceFile file, int width, CancellationToken ct)
    {
        if (_disposed) return null;
        if (width < 64) width = 320;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed || ct.IsCancellationRequested) return null;

            if (FindThumbnail(file, width) is { } hit)
            {
                var cached = await Task.Run(() => TryDecode(hit.Path, width), ct).ConfigureAwait(false);
                if (cached is not null) return new PreviewImage(cached, hit.Width, hit.Height);
                TryDelete(hit.Path);
            }

            var original = await ResolveOriginalAsync(file, ct).ConfigureAwait(false);
            if (original is null) return null;

            return await Task.Run(() => CreateThumbnail(file, original, width), ct)
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

    /// <summary>先读图头拿原始尺寸，再按不超过原图宽度解码，避免把小图放大、也避免大图占满内存。</summary>
    private PreviewImage? CreateThumbnail(SourceFile file, string originalPath, int maxWidth)
    {
        if (ReadSize(originalPath) is not { } source) return null;

        var bitmap = TryDecode(originalPath, Math.Min(maxWidth, source.Width));
        if (bitmap is null) return null;

        WriteThumbnail(bitmap, ThumbnailPath(file, maxWidth, source.Width, source.Height));
        return new PreviewImage(bitmap, source.Width, source.Height);
    }

    private string ThumbnailPath(SourceFile file, int width, int sourceWidth, int sourceHeight) =>
        Path.Combine(_cache.Root,
            $"thumb-{ImageCache.CreateKey(file)}-{width}-{sourceWidth}x{sourceHeight}.cache");

    /// <summary>缓存名形如 thumb-{key}-{宽度}-{原宽}x{原高}.cache，命中时直接拿到两项尺寸。</summary>
    private ThumbnailCacheHit? FindThumbnail(SourceFile file, int width)
    {
        var prefix = $"thumb-{ImageCache.CreateKey(file)}-{width}-";
        try
        {
            foreach (var path in Directory.EnumerateFiles(_cache.Root, prefix + "*.cache"))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (name.Length <= prefix.Length) continue;
                var parts = name[prefix.Length..].Split('x');
                if (parts.Length != 2 ||
                    !int.TryParse(parts[0], out var sourceWidth) ||
                    !int.TryParse(parts[1], out var sourceHeight)) continue;
                return new ThumbnailCacheHit(path, sourceWidth, sourceHeight);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            System.Security.SecurityException or ArgumentException)
        {
        }
        return null;
    }

    private static void WriteThumbnail(BitmapSource bitmap, string thumbnailPath)
    {
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
    }

    /// <summary>只读图头，不整张解码。</summary>
    private static (int Width, int Height)? ReadSize(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.SequentialScan);
            var decoder = BitmapDecoder.Create(stream,
                BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreImageCache,
                BitmapCacheOption.None);
            if (decoder.Frames.Count == 0) return null;
            var frame = decoder.Frames[0];
            return frame.PixelWidth > 0 && frame.PixelHeight > 0
                ? (frame.PixelWidth, frame.PixelHeight)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            NotSupportedException or FileFormatException or ArgumentException)
        {
            return null;
        }
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
            // 注意：StreamSource 不能搭配 IgnoreImageCache，WPF 会在 EndInit 抛 ArgumentNullException。
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

    private sealed record ThumbnailCacheHit(string Path, int Width, int Height);
}
