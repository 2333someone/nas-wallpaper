using System.IO;
using NasWallpaper.Models;

namespace NasWallpaper.Services.Sources;

public interface IImageSource
{
    IAsyncEnumerable<SourceFile> ScanAsync(CancellationToken ct);
    Task<Stream> OpenReadAsync(SourceFile file, CancellationToken ct);

    /// <summary>删除图片源上的原始文件（SMB/本地走文件系统，WebDAV 走 HTTP DELETE）。</summary>
    Task DeleteAsync(string location, CancellationToken ct);

    static bool IsImagePath(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".tif", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".tiff", StringComparison.OrdinalIgnoreCase);
    }
}
