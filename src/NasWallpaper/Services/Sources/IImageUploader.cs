namespace NasWallpaper.Services.Sources;

/// <summary>
/// 可写入的图片源：把本地图片放进源目录（拖拽导入用）。
/// 只读源不实现这个接口，调用方据此给出“当前图片源不支持写入”的提示。
/// </summary>
public interface IImageUploader
{
    /// <summary>
    /// 把本地文件复制/上传到图片源根目录，返回新文件在源中的位置。
    /// 同名文件已存在时自动追加序号，绝不覆盖源里已有的图片。
    /// </summary>
    Task<string> UploadAsync(string localPath, CancellationToken ct);
}
