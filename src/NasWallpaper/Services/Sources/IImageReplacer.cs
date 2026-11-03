using System.IO;

namespace NasWallpaper.Services.Sources;

/// <summary>
/// 可原地覆盖的图片源：用来把旋转后的图片写回同一个文件。
/// 实现方必须保证先写临时文件再替换，避免中途失败把原图写坏。
/// </summary>
public interface IImageReplacer
{
    /// <summary>用新内容覆盖源上的已有文件，文件名保持不变。</summary>
    Task ReplaceAsync(string location, Stream content, CancellationToken ct);
}
