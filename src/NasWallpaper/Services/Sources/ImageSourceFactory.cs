using NasWallpaper.Models;

namespace NasWallpaper.Services.Sources;

public static class ImageSourceFactory
{
    public static IImageSource Create(SourceConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrWhiteSpace(config.Address))
            throw new ArgumentException("图片源地址不能为空。", nameof(config));

        return config.Kind switch
        {
            SourceKind.Smb or SourceKind.Local => new FileImageSource(config),
            SourceKind.WebDav => new WebDavImageSource(config),
            _ => throw new NotSupportedException($"不支持的图片源类型：{config.Kind}")
        };
    }
}
